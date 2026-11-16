using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>What a translated body belongs to.</summary>
public enum BodyKind
{
    /// <summary>A script batch run as a <c>DO</c> block: no parameters, cannot return rows.</summary>
    AnonymousBlock,
    Procedure,
    ScalarFunction,

    /// <summary>A multi-statement table-valued function: rows go to its return table, RETURN returns them.</summary>
    TableFunction,
}

/// <summary>A routine parameter as its body sees it.</summary>
/// <param name="SourceName">The T-SQL name without <c>@</c>.</param>
/// <param name="PgName">The PostgreSQL parameter name.</param>
/// <param name="DataType">The declared type, for conversions on assignment.</param>
public sealed record BodyParameter(string SourceName, string PgName, SqlServerType Type, DataTypeReference? DataType);

/// <param name="RoutineName">The PostgreSQL routine name parameters are qualified with (null for a DO block).</param>
/// <param name="ReturnType">A scalar function's return type.</param>
public sealed record BodySignature(BodyKind Kind, string? RoutineName, IReadOnlyList<BodyParameter> Parameters,
    SqlServerType? ReturnType = null, DataTypeReference? ReturnDataType = null)
{
    public static readonly BodySignature AnonymousBlock = new(BodyKind.AnonymousBlock, null, []);

    /// <summary>A multi-statement TVF's <c>RETURNS @t TABLE (…)</c>.</summary>
    public DeclareTableVariableBody? ReturnTable { get; init; }
}

/// <summary>
/// Translates T-SQL procedural code — variables, assignment, IF, WHILE, PRINT, RAISERROR, THROW,
/// and the DML and queries between them — to a PL/pgSQL body. Parameters are written
/// <c>routine.param</c> and variables <c>v_name</c>, all declared at the top of the body, with
/// <c>#variable_conflict use_column</c> so a bare name is a column as in T-SQL. Statements not
/// translated yet are kept as comments with their diagnostic code.
/// </summary>
public sealed partial class ProceduralTranslator
{
    private const string RowVariable = "sql2pg_row";
    private const string RowCountVariable = "sql2pg_rowcount";
    private const string IdentityVariable = "sql2pg_identity";

    private readonly QueryTranslator _queries;
    private readonly ExpressionTranslator _x;
    private readonly SemanticModel _model;
    private readonly NameMapper _names;

    public ProceduralTranslator(QueryTranslator queries)
    {
        _queries = queries;
        _x = queries.Expressions;
        _model = _x.Model;
        _names = _x.Names;
    }

    /// <summary>A variable of the body.</summary>
    private sealed record Variable(string PgName, SqlServerType? Type, DataTypeReference? DataType, string PgType);

    /// <summary>Everything one body's translation accumulates.</summary>
    private sealed class BodyState(BodySignature signature, ExpressionContext context)
    {
        public BodySignature Signature { get; } = signature;
        public ExpressionContext C { get; } = context;
        public List<PgDeclaration> Declarations { get; } = [];
        public Dictionary<string, Variable> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> TakenNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Assigned { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Helpers { get; } = new(StringComparer.Ordinal);
        public HashSet<PgExpr> VariableRefs { get; } = new(ReferenceEqualityComparer.Instance);
        public bool Prologue { get; set; } = true;
        public int LoopDepth { get; set; }
        public int TryDepth { get; set; }
        public bool ReadsRowCount { get; init; }
        public bool ReadsIdentity { get; init; }
        public bool HasTry { get; init; }
        public bool XactAbortOn { get; set; }
        public bool StatementAfterDml { get; set; }

        /// <summary>Table variables (<c>@t</c>) and the temporary tables they are kept in.</summary>
        public Dictionary<string, string> TableVariables { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A table function's return table.</summary>
        public string? ReturnTable { get; set; }
    }

    /// <summary>Translates a body's statements to one outer block with every declaration hoisted to it.</summary>
    /// <param name="context">The context diagnostics go to; its scope becomes the body's scope (variables are defined in it).</param>
    public PgPlBody Body(IList<TSqlStatement> statements, BodySignature signature, ExpressionContext context)
    {
        var finder = new GlobalFinder();
        foreach (var s in statements)
            s.Accept(finder);
        var st = new BodyState(signature, context)
        {
            ReadsRowCount = finder.RowCount,
            ReadsIdentity = finder.Identity,
            HasTry = finder.Try,
        };
        return WithHooks(st, () =>
        {
            var body = new List<PgPlStatement>();
            if (signature.ReturnTable is { } table)
            {
                st.ReturnTable = "_ret_" + signature.RoutineName;
                body.AddRange(TableVariable(table.VariableName.Value, st.ReturnTable, table.Definition, st, onCommit: "DROP"));
            }
            body.AddRange(List(statements, st, topLevel: true));
            if (st.StatementAfterDml && !st.XactAbortOn && !st.HasTry)
                context.Report(DiagnosticCodes.ErrorStopsExecution, Severity.Info,
                    "Statements follow DML without SET XACT_ABORT ON or TRY: SQL Server continues after most statement errors, PostgreSQL stops at the first.");
            return new PgPlBody(new PgBlock(body) { Declarations = st.Declarations }) { Directives = ["#variable_conflict use_column"] };
        });
    }

    /// <summary>
    /// A scalar function's single <c>RETURN expression</c>, for a <c>LANGUAGE sql</c> body:
    /// converted to the return type, parameters written <c>routine.param</c>.
    /// </summary>
    public PgExpr ReturnExpression(ScalarExpression value, BodySignature signature, ExpressionContext context)
    {
        var st = new BodyState(signature, context);
        return WithHooks(st, () => Assigned(value, signature.ReturnType, signature.ReturnDataType, context));
    }

    /// <summary>A constant such as a parameter's default (no variables in scope).</summary>
    public PgExpr Constant(ScalarExpression value, ExpressionContext context) => _x.Scalar(value, context);

    /// <summary>An inline table-valued function's query, for a <c>LANGUAGE sql</c> body.</summary>
    public PgQuery Query(SelectStatement select, BodySignature signature, ExpressionContext context)
    {
        var st = new BodyState(signature, context);
        return WithHooks(st, () => _queries.Query(select.QueryExpression, context, select.WithCtesAndXmlNamespaces));
    }

    /// <summary>Runs a translation with the body's parameters, variables and globals known to the expression and query translators.</summary>
    private T WithHooks<T>(BodyState st, Func<T> translate)
    {
        foreach (var p in st.Signature.Parameters)
            st.C.Scope.DefineVariable(p.SourceName, p.Type);
        var saved = (_x.VariableWriter, _x.ProceduralGlobal, _x.ProceduralFunction, _queries.TableVariable);
        _x.VariableWriter = name => VariableRef(name, st);
        _x.ProceduralGlobal = (name, c) => Global(name, c, st);
        _x.ProceduralFunction = (name, f, c) => Function(name, f, c, st);
        _queries.TableVariable = name => st.TableVariables.GetValueOrDefault(name);
        try
        {
            return translate();
        }
        finally
        {
            (_x.VariableWriter, _x.ProceduralGlobal, _x.ProceduralFunction, _queries.TableVariable) = saved;
        }
    }

    /// <summary>
    /// A table variable as a temporary table: dropped if a previous call left it, then created
    /// with the declared columns (types, NOT NULL, defaults, identity) and keys.
    /// </summary>
    private List<PgPlStatement> TableVariable(string variable, string table, TableDefinition definition, BodyState st, string onCommit)
    {
        var c = st.C;
        var name = new PgName("", table);
        var columns = new List<PgColumnDef>();
        var relationColumns = new List<ColumnInfo>();
        var keys = new List<PgStatement>();
        foreach (var col in definition.ColumnDefinitions)
        {
            if (col.ComputedColumnExpression is not null || col.DataType is null)
                throw ExpressionTranslator.Untranslatable(col, "a computed column in a table variable");
            var type = _model.TypeOf(col.DataType, c.Schema);
            var mapped = _x.Types.Map(type with { UserTypeName = null }, c.Owner);
            string column = _names.Alias(col.ColumnIdentifier.Value);
            bool notNull = col.Constraints.OfType<NullableConstraintDefinition>().Any(n => !n.Nullable)
                           || col.Constraints.OfType<UniqueConstraintDefinition>().Any(u => u.IsPrimaryKey);
            PgIdentity? identity = col.IdentityOptions is { } id
                ? new PgIdentity(Seed(id.IdentitySeed), Seed(id.IdentityIncrement))
                : null;
            string pgType = identity is not null && mapped.PgType.StartsWith("numeric", StringComparison.Ordinal) ? "bigint" : mapped.PgType;
            PgExpr? defaultValue = col.DefaultConstraint?.Expression is { } d ? Assigned(d, type, col.DataType, c) : null;
            columns.Add(new PgColumnDef(column, pgType, notNull, defaultValue, identity));
            relationColumns.Add(new ColumnInfo(col.ColumnIdentifier.Value, type));
            foreach (var key in col.Constraints.OfType<UniqueConstraintDefinition>())
                keys.Add(Key(name, table, key.IsPrimaryKey, [column], keys.Count));
        }
        foreach (var key in definition.TableConstraints.OfType<UniqueConstraintDefinition>())
            keys.Add(Key(name, table, key.IsPrimaryKey, key.Columns.Select(k => _names.Alias(k.Column.MultiPartIdentifier.Identifiers[^1].Value)).ToList(), keys.Count));

        st.TableVariables[variable] = table;
        c.Scope.DefineTable(variable, new RelationInfo(null, ObjectKind.Table, relationColumns));
        return
        [
            new PgSqlStatement(new PgDropTable(name)),
            new PgSqlStatement(new PgCreateTable(name, columns) { Temporary = true, OnCommit = onCommit }),
            .. keys.Select(k => new PgSqlStatement(k)),
        ];
    }

    private static long Seed(ScalarExpression? e) =>
        e is null ? 1 : long.Parse(ExpressionTranslator.Text(e).Trim('(', ')', ' '), System.Globalization.CultureInfo.InvariantCulture);

    private static PgStatement Key(PgName table, string tableName, bool primary, IReadOnlyList<string> columns, int n) =>
        primary
            ? new PgAddPrimaryKey(table, tableName + "_pkey", columns)
            : new PgAddUnique(table, $"{tableName}_key{n}", columns);

    /// <summary>Whether a statement needs procedural translation (a script batch containing one becomes a DO block).</summary>
    public static bool IsProcedural(TSqlStatement statement) => statement switch
    {
        SelectStatement s => s.QueryExpression is QuerySpecification spec && spec.SelectElements.OfType<SelectSetVariable>().Any(),
        InsertStatement or UpdateStatement or DeleteStatement or MergeStatement => false,
        UseStatement or PredicateSetStatement or SetCommandStatement or SetRowCountStatement
            or SetTransactionIsolationLevelStatement or SetIdentityInsertStatement => false,
        CreateTableStatement or AlterTableStatement or CreateIndexStatement or CreateViewStatement or CreateTypeStatement
            or CreateSequenceStatement or CreateSchemaStatement or DropObjectsStatement => false,
        _ => true,
    };

    // ---- statement lists ----

    private List<PgPlStatement> List(IList<TSqlStatement> statements, BodyState st, bool topLevel = false)
    {
        var result = new List<PgPlStatement>();
        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            // Session options produce no code, so DECLAREs after SET NOCOUNT ON still open the body.
            if (topLevel && s is not (DeclareVariableStatement or PredicateSetStatement or SetCommandStatement))
                st.Prologue = false;
            var next = i + 1 < statements.Count ? statements[i + 1] : null;
            result.AddRange(Statement(s, next, st));
            if (IsDml(s) && next is not null)
                st.StatementAfterDml = true;
        }
        return result;
    }

    /// <summary>DML on a real table (a table variable's rows are the routine's own, and rarely fail).</summary>
    private static bool IsDml(TSqlStatement s) => s switch
    {
        InsertStatement i => i.InsertSpecification.Target is not VariableTableReference,
        UpdateStatement u => u.UpdateSpecification.Target is not VariableTableReference,
        DeleteStatement d => d.DeleteSpecification.Target is not VariableTableReference,
        MergeStatement or SelectStatement { Into: not null } => true,
        _ => false,
    };

    private IEnumerable<PgPlStatement> Statement(TSqlStatement s, TSqlStatement? next, BodyState st)
    {
        try
        {
            var translated = Translate(s, next, st);
            // PRINT, RAISERROR and SET options set @@ROWCOUNT to 0 (after their own arguments are read).
            if (st.ReadsRowCount && s is PrintStatement or RaiseErrorStatement or PredicateSetStatement or SetCommandStatement or UseStatement)
                translated = translated.Append(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("0")));
            return translated;
        }
        catch (UntranslatableException ex)
        {
            return [Untranslated(s, ex.Code, ex.Message, st)];
        }
    }

    private PgPlStatement Untranslated(TSqlFragment s, string code, string message, BodyState st, Severity severity = Severity.Error)
    {
        st.C.Report(code, severity, message);
        return new PgSqlStatement(new PgUntranslated(code, ExpressionTranslator.Text(s)));
    }

    private IEnumerable<PgPlStatement> Translate(TSqlStatement s, TSqlStatement? next, BodyState st)
    {
        var c = st.C;
        switch (s)
        {
            case DeclareVariableStatement d:
                return Declare(d, st);
            case SetVariableStatement set:
                return SetVariable(set, st);
            case SelectStatement select when select.QueryExpression is QuerySpecification spec && spec.SelectElements.OfType<SelectSetVariable>().Any():
                return SelectAssign(select, spec, st);
            case SelectStatement { Into: null } select:
                return ResultSet(select, st);
            case SelectStatement or InsertStatement or UpdateStatement or DeleteStatement or MergeStatement:
                return Dml(s, st);
            case BeginEndBlockStatement block:
                return List(block.StatementList.Statements, st);
            case IfStatement i:
                return [If(i, st)];
            case WhileStatement w:
            {
                var condition = _x.Boolean(w.Predicate, c);
                st.LoopDepth++;
                try
                {
                    return [new PgWhile(condition, Nested(w.Statement, st))];
                }
                finally
                {
                    st.LoopDepth--;
                }
            }
            case BreakStatement:
                return [new PgExit()];
            case ContinueStatement:
                return [new PgContinue()];
            case ReturnStatement r:
                return Return(r, st);
            case PrintStatement { Expression: StringLiteral literal }:
                return [new PgRaise("NOTICE", "%") { Arguments = [PgLiteral.String(literal.Value)] }];
            case PrintStatement p:
                // PRINT NULL prints an empty line; RAISE would print <NULL>.
                return [new PgRaise("NOTICE", "%") { Arguments = [new PgFunctionCall("COALESCE", [PrintText(p.Expression, c), PgLiteral.String("")])] }];
            case RaiseErrorStatement r:
                return [RaiseError(r, next, st)];
            case ThrowStatement t:
                return [Throw(t, c)];
            case PredicateSetStatement p:
                if ((p.Options & SetOptions.XactAbort) != 0)
                    st.XactAbortOn = p.IsOn;
                if ((p.Options & ~(SetOptions.NoCount | SetOptions.XactAbort)) != 0)
                    c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"'{ExpressionTranslator.Text(s).Trim()}' sets a SQL Server session option and is dropped.");
                return [];
            case SetCommandStatement or SetTransactionIsolationLevelStatement:
                c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"'{ExpressionTranslator.Text(s).Trim()}' sets a SQL Server session option and is dropped.");
                return [];
            case SetRowCountStatement { NumberRows: IntegerLiteral { Value: "0" } }:
                return [];
            case UseStatement use:
                c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"USE {use.DatabaseName.Value} is dropped: a PostgreSQL connection stays in its database.");
                return [];
            case WaitForStatement w:
                return [WaitFor(w, st)];
            case GoToStatement or LabelStatement:
                return [Untranslated(s, DiagnosticCodes.GotoLabel, "GOTO and labels have no PL/pgSQL equivalent.", st)];
            case CreateTableStatement or AlterTableStatement or CreateIndexStatement or CreateViewStatement or CreateTypeStatement
                or CreateSequenceStatement or CreateSchemaStatement or DropObjectsStatement
                when st.Signature.Kind == BodyKind.AnonymousBlock && !IsTempTableDdl(s):
                return [Untranslated(s, DiagnosticCodes.DdlInScript,
                    "DDL in an ad-hoc script: convert schema objects with 'sql2pg schema', which orders them and creates their keys and indexes.", st)];
            default:
                return [Untranslated(s, DiagnosticCodes.ProceduralStatement, $"'{s.GetType().Name}' is not translated yet.", st)];
        }
    }

    private static bool IsTempTableDdl(TSqlStatement s) =>
        s is CreateTableStatement create && create.SchemaObjectName.BaseIdentifier.Value.StartsWith('#');

    /// <summary>A nested statement (IF/WHILE body): a BEGIN … END is flattened into its statements.</summary>
    private List<PgPlStatement> Nested(TSqlStatement s, BodyState st) =>
        s is BeginEndBlockStatement block ? List(block.StatementList.Statements, st) : List([s], st);

    // ---- variables ----

    private PgExpr VariableRef(string name, BodyState st)
    {
        PgExpr reference = st.Variables.TryGetValue(name, out var v)
            ? new PgColumnRef(v.PgName)
            : st.Signature.Parameters.FirstOrDefault(p => string.Equals(p.SourceName, name, StringComparison.OrdinalIgnoreCase)) is { } p
                ? new PgColumnRef(p.PgName, st.Signature.RoutineName)
                : new PgColumnRef("v_" + _names.Alias(name));
        st.VariableRefs.Add(reference);
        return reference;
    }

    /// <summary>The PostgreSQL target a variable or parameter is assigned through, with its declared type.</summary>
    private (PgExpr Target, SqlServerType? Type, DataTypeReference? DataType) Target(VariableReference v, BodyState st)
    {
        string name = v.Name.TrimStart('@');
        if (st.Variables.TryGetValue(name, out var variable))
            return (new PgColumnRef(variable.PgName), variable.Type, variable.DataType);
        if (st.Signature.Parameters.FirstOrDefault(p => string.Equals(p.SourceName, name, StringComparison.OrdinalIgnoreCase)) is { } p)
            return (new PgColumnRef(p.PgName, st.Signature.RoutineName), p.Type, p.DataType);
        throw ExpressionTranslator.Untranslatable(v, $"{v.Name}, which is not declared,");
    }

    private IEnumerable<PgPlStatement> Declare(DeclareVariableStatement d, BodyState st)
    {
        var c = st.C;
        var result = new List<PgPlStatement>();
        foreach (var e in d.Declarations)
        {
            string name = e.VariableName.Value.TrimStart('@');
            if (e.DataType is SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Cursor })
            {
                result.Add(Untranslated(e, DiagnosticCodes.ProceduralStatement, "A cursor variable is not translated yet.", st));
                continue;
            }
            var type = _model.TypeOf(e.DataType, c.Schema);
            var mapped = _x.Types.Map(type with { UserTypeName = null }, c.Owner);
            foreach (var diagnostic in mapped.Diagnostics.Where(x => x.Severity != Severity.Info))
                c.Diagnostics.Add(diagnostic with { Object = c.Owner });
            if (mapped.Extension is not null)
                c.Extensions.Add(mapped.Extension);

            PgExpr? value = e.Value is null ? null : Assigned(e.Value, type, e.DataType, c);
            if (st.Variables.TryGetValue(name, out var existing))
            {
                if (existing.PgType != mapped.PgType)
                {
                    result.Add(Untranslated(e, DiagnosticCodes.VariableRedeclared,
                        $"@{name} is declared again as {mapped.PgType} (first as {existing.PgType}); a PL/pgSQL body declares each variable once.", st));
                    continue;
                }
            }
            else
            {
                var variable = new Variable(UniqueName("v_" + _names.Alias(name), st), type, e.DataType, mapped.PgType);
                st.Variables[name] = variable;
                c.Scope.DefineVariable(name, type);
                st.Declarations.Add(new PgDeclare(variable.PgName, mapped.PgType, st.Prologue ? value : null));
                if (st.Prologue)
                {
                    if (value is not null)
                        st.Assigned.Add(name);
                    continue;
                }
            }
            if (value is not null)
            {
                result.Add(new PgAssign(new PgColumnRef(st.Variables[name].PgName), value));
                st.Assigned.Add(name);
            }
        }
        return result;
    }

    private static string UniqueName(string name, BodyState st)
    {
        string candidate = name;
        for (int n = 2; !st.TakenNames.Add(candidate); n++)
            candidate = $"{name}_{n}";
        return candidate;
    }

    /// <summary>A helper variable (row count, last identity, loop record), declared once when first used.</summary>
    private static PgColumnRef Helper(string name, string type, BodyState st)
    {
        if (st.Helpers.Add(name))
            st.Declarations.Add(new PgDeclare(name, type));
        return new PgColumnRef(name);
    }

    /// <summary>
    /// A value assigned to something of <paramref name="target"/> type, converted as SQL Server
    /// converts it implicitly (a decimal assigned to an int is truncated, a string to a shorter
    /// varchar is cut) — the same as an explicit CAST — when its own type differs.
    /// </summary>
    private PgExpr Assigned(ScalarExpression value, SqlServerType? target, DataTypeReference? dataType, ExpressionContext c) =>
        _x.Scalar(Converted(value, target, dataType, c), c);

    private ScalarExpression Converted(ScalarExpression value, SqlServerType? target, DataTypeReference? dataType, ExpressionContext c)
    {
        if (target is null || dataType is null || value is NullLiteral)
            return value;
        var source = _x.TypeOf(value, c);
        if (source is null || !TypeRules.NeedsConversion(source, target))
            return value;
        return new CastCall { Parameter = value, DataType = dataType };
    }

    private IEnumerable<PgPlStatement> SetVariable(SetVariableStatement set, BodyState st)
    {
        if (set.CursorDefinition is not null)
            throw ExpressionTranslator.Untranslatable(set, "SET @cursor = CURSOR …", DiagnosticCodes.ProceduralStatement);
        if (set.FunctionCallExists || set.Identifier is not null)
            throw ExpressionTranslator.Untranslatable(set, "a method call on a variable (SET @x.method(…))");
        var (target, type, dataType) = Target(set.Variable, st);
        var value = Compound(set.Variable, set.AssignmentKind, set.Expression);
        var result = new List<PgPlStatement> { new PgAssign(target, Assigned(value, type, dataType, st.C)) };
        st.Assigned.Add(set.Variable.Name.TrimStart('@'));
        if (st.ReadsRowCount)
            result.Add(new PgAssign(Helper(RowCountVariable, "integer", st), new PgLiteral("1")));
        return result;
    }

    /// <summary><c>@x += e</c> is <c>@x = @x + e</c> (string concatenation for strings, as the + operator decides).</summary>
    private static ScalarExpression Compound(VariableReference variable, AssignmentKind kind, ScalarExpression value)
    {
        BinaryExpressionType? op = kind switch
        {
            AssignmentKind.AddEquals => BinaryExpressionType.Add,
            AssignmentKind.SubtractEquals => BinaryExpressionType.Subtract,
            AssignmentKind.MultiplyEquals => BinaryExpressionType.Multiply,
            AssignmentKind.DivideEquals => BinaryExpressionType.Divide,
            AssignmentKind.ModEquals => BinaryExpressionType.Modulo,
            AssignmentKind.BitwiseAndEquals => BinaryExpressionType.BitwiseAnd,
            AssignmentKind.BitwiseOrEquals => BinaryExpressionType.BitwiseOr,
            AssignmentKind.BitwiseXorEquals => BinaryExpressionType.BitwiseXor,
            _ => null,
        };
        return op is null ? value : new BinaryExpression
        {
            BinaryExpressionType = op.Value,
            FirstExpression = new VariableReference { Name = variable.Name },
            SecondExpression = new ParenthesisExpression { Expression = value },
        };
    }

    // ---- control flow ----

    private PgIf If(IfStatement i, BodyState st)
    {
        var branches = new List<PgIfBranch>();
        TSqlStatement? elseStatement = i;
        while (elseStatement is IfStatement current)
        {
            branches.Add(new PgIfBranch(_x.Boolean(current.Predicate, st.C), Nested(current.ThenStatement, st)));
            elseStatement = current.ElseStatement;
        }
        return new PgIf(branches, elseStatement is null ? null : Nested(elseStatement, st));
    }

    private IEnumerable<PgPlStatement> Return(ReturnStatement r, BodyState st)
    {
        var sig = st.Signature;
        if (sig.Kind == BodyKind.TableFunction && st.ReturnTable is { } table)
            return
            [
                new PgReturnQuery(new PgSelect { Targets = [PgTarget.All()], From = [new PgTableRef(new PgName("", table))] }),
                new PgReturn(),
            ];
        if (sig.Kind == BodyKind.ScalarFunction)
            return r.Expression is null
                ? throw ExpressionTranslator.Untranslatable(r, "RETURN without a value in a scalar function")
                : [new PgReturn(Assigned(r.Expression, sig.ReturnType, sig.ReturnDataType, st.C))];
        if (r.Expression is not null)
            throw ExpressionTranslator.Untranslatable(r, "RETURN with a status code", DiagnosticCodes.ProceduralStatement);
        return [new PgReturn()];
    }

    /// <summary>
    /// WAITFOR DELAY 'hh:mm:ss[.fff]' sleeps that long: <c>pg_sleep(seconds)</c>, the seconds
    /// computed here for a literal and by PostgreSQL (as an interval) otherwise.
    /// </summary>
    private PgPlStatement WaitFor(WaitForStatement w, BodyState st)
    {
        if (w.WaitForOption != WaitForOption.Delay || w.Parameter is null)
            throw ExpressionTranslator.Untranslatable(w, "WAITFOR other than DELAY", DiagnosticCodes.ProceduralStatement);
        PgExpr seconds = w.Parameter is StringLiteral s && TimeSpan.TryParse(s.Value, System.Globalization.CultureInfo.InvariantCulture, out var delay)
            ? new PgLiteral(delay.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
            : new PgTemplate("extract(epoch FROM CAST({0} AS interval))", [_x.Scalar(w.Parameter, st.C)]);
        return new PgPerform(new PgSelect { Targets = [new PgTarget(new PgFunctionCall("pg_sleep", [seconds]))] });
    }

    // ---- messages and errors ----

    /// <summary>PRINT converts its argument to a string as SQL Server does (a bit is 1/0, not true/false).</summary>
    private PgExpr PrintText(ScalarExpression e, ExpressionContext c)
    {
        var type = _x.TypeOf(e, c);
        if (type is null || TypeRules.IsString(type))
            return _x.Scalar(e, c);
        var nvarchar = new SqlDataTypeReference { SqlDataTypeOption = SqlDataTypeOption.NVarChar };
        nvarchar.Parameters.Add(new IntegerLiteral { Value = "4000" });
        return _x.Scalar(new CastCall { Parameter = e, DataType = nvarchar }, c);
    }

    private PgPlStatement RaiseError(RaiseErrorStatement r, TSqlStatement? next, BodyState st)
    {
        var c = st.C;
        int? severity = r.SecondParameter is IntegerLiteral sev && int.TryParse(sev.Value, out int n) ? n : null;
        string level = severity is <= 10 ? "NOTICE" : "EXCEPTION";
        var raise = Raise(r, level, c);
        if (level == "EXCEPTION" && st.TryDepth == 0 && next is not null and not ReturnStatement)
            c.Report(DiagnosticCodes.RaiserrorStops, Severity.Info,
                "RAISERROR with severity above 10 ends the routine in PostgreSQL; SQL Server continues with the next statement.");
        return raise;
    }

    private PgRaise Raise(RaiseErrorStatement r, string level, ExpressionContext c)
    {
        var args = r.OptionalParameters.Select(a => _x.Scalar(a, c)).ToList();
        switch (r.FirstParameter)
        {
            case StringLiteral message:
                string format = RaiserrorFormat(message.Value, out bool simplified);
                if (simplified)
                    c.Report(DiagnosticCodes.RaiserrorFormat, Severity.Warning,
                        $"The RAISERROR message '{message.Value}' uses flags, widths, precisions or %x/%o; PostgreSQL writes each argument in its plain form.");
                int placeholders = CountPlaceholders(format);
                if (placeholders != args.Count)
                    throw ExpressionTranslator.Untranslatable(r, $"RAISERROR with {placeholders} placeholders and {args.Count} arguments", DiagnosticCodes.RaiserrorUntranslatable);
                return new PgRaise(level, format) { Arguments = args };
            case IntegerLiteral:
                throw ExpressionTranslator.Untranslatable(r, "RAISERROR with a message number (its text is in sys.messages)", DiagnosticCodes.RaiserrorUntranslatable);
            default:
                if (args.Count > 0)
                    throw ExpressionTranslator.Untranslatable(r, "RAISERROR with a non-literal message and arguments", DiagnosticCodes.RaiserrorUntranslatable);
                return new PgRaise(level, "%") { Arguments = [_x.Text(r.FirstParameter, c)] };
        }
    }

    /// <summary>
    /// RAISERROR's printf-style message as RAISE's: each <c>%[flags][width][.precision][h|l]type</c>
    /// becomes <c>%</c>; <c>%%</c> stays. A literal <c>%</c> not followed by a type is doubled.
    /// </summary>
    internal static string RaiserrorFormat(string message, out bool simplified)
    {
        simplified = false;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] != '%')
            {
                sb.Append(message[i]);
                continue;
            }
            if (i + 1 < message.Length && message[i + 1] == '%')
            {
                sb.Append("%%");
                i++;
                continue;
            }
            int j = i + 1;
            while (j < message.Length && message[j] is '-' or '+' or '0' or ' ' or '#')
                j++;
            while (j < message.Length && (char.IsDigit(message[j]) || message[j] == '*'))
                j++;
            if (j < message.Length && message[j] == '.')
            {
                j++;
                while (j < message.Length && (char.IsDigit(message[j]) || message[j] == '*'))
                    j++;
            }
            bool modifiers = j > i + 1;
            if (j < message.Length && message[j] is 'h' or 'l')
                j++;
            if (j < message.Length && message[j] is 'd' or 'i' or 'o' or 's' or 'u' or 'x' or 'X')
            {
                simplified |= modifiers || message[j] is 'o' or 'x' or 'X';
                sb.Append('%');
                i = j;
            }
            else
            {
                sb.Append("%%");
            }
        }
        return sb.ToString();
    }

    private static int CountPlaceholders(string format)
    {
        int count = 0;
        for (int i = 0; i < format.Length; i++)
        {
            if (format[i] != '%')
                continue;
            if (i + 1 < format.Length && format[i + 1] == '%')
                i++;
            else
                count++;
        }
        return count;
    }

    /// <summary>
    /// <c>THROW n, msg, state</c> raises with the message, SQLSTATE P0001 and the T-SQL error number
    /// in DETAIL (<c>mssql_error=n</c>); a bare THROW re-raises the error being handled.
    /// </summary>
    private PgPlStatement Throw(ThrowStatement t, ExpressionContext c)
    {
        if (t.ErrorNumber is null)
            return PgRaise.Rethrow;
        PgExpr detail = t.ErrorNumber is IntegerLiteral number
            ? PgLiteral.String("mssql_error=" + number.Value)
            : new PgBinary("||", PgLiteral.String("mssql_error="), new PgCast(_x.Scalar(t.ErrorNumber, c), "text"));
        return new PgRaise("EXCEPTION")
        {
            Using = [new("MESSAGE", _x.Text(t.Message, c)), new("ERRCODE", PgLiteral.String("P0001")), new("DETAIL", detail)],
        };
    }

    // ---- globals and functions ----

    private PgExpr? Global(string name, ExpressionContext c, BodyState st)
    {
        switch (name)
        {
            case "@@ROWCOUNT":
                return Helper(RowCountVariable, "integer", st);
            case "@@ERROR":
                c.Report(DiagnosticCodes.ErrorGlobal, Severity.Warning,
                    "@@ERROR is 0 here: had the previous statement failed, PostgreSQL would have left the routine already.");
                return new PgLiteral("0");
            case "@@IDENTITY":
                return Helper(IdentityVariable, "numeric(38,0)", st);
            default:
                return null;
        }
    }

    private PgExpr? Function(string name, FunctionCall f, ExpressionContext c, BodyState st) => name switch
    {
        "SCOPE_IDENTITY" when f.Parameters.Count == 0 => Helper(IdentityVariable, "numeric(38,0)", st),
        _ => null,
    };

    /// <summary>Finds what a body reads that needs bookkeeping after statements: @@ROWCOUNT, the last identity, TRY.</summary>
    private sealed class GlobalFinder : TSqlFragmentVisitor
    {
        public bool RowCount { get; private set; }
        public bool Identity { get; private set; }
        public bool Try { get; private set; }

        public override void Visit(GlobalVariableExpression node)
        {
            string name = node.Name.ToUpperInvariant();
            RowCount |= name == "@@ROWCOUNT";
            Identity |= name == "@@IDENTITY";
        }

        public override void Visit(FunctionCall node) =>
            Identity |= string.Equals(node.FunctionName.Value, "SCOPE_IDENTITY", StringComparison.OrdinalIgnoreCase);

        public override void Visit(TryCatchStatement node) => Try = true;
    }
}
