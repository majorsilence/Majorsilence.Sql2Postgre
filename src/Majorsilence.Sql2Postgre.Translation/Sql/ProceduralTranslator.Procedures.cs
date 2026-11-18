using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>A procedure parameter as the procedure's callers see it.</summary>
/// <param name="SourceName">The T-SQL name without <c>@</c>.</param>
/// <param name="PgName">The PostgreSQL parameter name.</param>
/// <param name="Output">An OUTPUT parameter (INOUT in PostgreSQL).</param>
/// <param name="Default">The T-SQL default, if any.</param>
public sealed record ProcedureParameterShape(string SourceName, string PgName, SqlServerType Type, string PgType, bool Output, ScalarExpression? Default);

/// <summary>
/// What a converted procedure looks like from outside: a PostgreSQL procedure (result sets, if
/// any, come back through INOUT refcursor parameters; a status code through INOUT return_code), or
/// a set-returning function when it returns one result set and nothing else.
/// </summary>
/// <param name="IsFunction">Converted to <c>FUNCTION … RETURNS TABLE</c>.</param>
public sealed record ProcedureShape(ObjectName Name, PgName PgName, bool IsFunction, IReadOnlyList<ProcedureParameterShape> Parameters)
{
    /// <summary>The INOUT parameter the status code of <c>RETURN n</c> goes to, if the procedure returns one.</summary>
    public string? ReturnCode { get; init; }

    /// <summary>The INOUT refcursor parameters, one per result set, in order (a procedure only).</summary>
    public IReadOnlyList<string> Cursors { get; init; } = [];

    /// <summary>The result columns of a procedure converted to a function (names unique, types known).</summary>
    public IReadOnlyList<ColumnInfo> Columns { get; init; } = [];

    /// <summary>Each statement returning rows to the caller, with the number of its first result set.</summary>
    internal IReadOnlyDictionary<TSqlStatement, int> ResultSets { get; init; } = new Dictionary<TSqlStatement, int>();

    /// <summary>The number of result sets the procedure returns.</summary>
    public int ResultSetCount { get; init; }

    /// <summary>The procedure ends a transaction (BEGIN TRAN, COMMIT or ROLLBACK, itself or in a procedure it calls): it must be CALLed, outside an exception block.</summary>
    public bool Commits { get; init; }

    /// <summary>The temp tables (<c>#t</c>) the procedure creates.</summary>
    internal IReadOnlySet<string> TempTables { get; init; } = new HashSet<string>();
}

/// <summary>Procedures: their shape, result sets, status codes, EXEC and transactions.</summary>
public sealed partial class ProceduralTranslator
{
    private static readonly SqlServerType IntType = new("int");
    private readonly Dictionary<ObjectName, ProcedureShape?> _shapes = new(ObjectNameComparer.Instance);

    /// <summary>
    /// The shape of a procedure of the source database, worked out once from its body: the
    /// statements returning rows, RETURN with a value, COMMIT/ROLLBACK and OUTPUT parameters decide
    /// between a procedure and a function. Null when <paramref name="name"/> is not a T-SQL procedure.
    /// </summary>
    public ProcedureShape? Shape(ObjectName name)
    {
        if (_shapes.TryGetValue(name, out var known))
            return known;
        _shapes[name] = null; // while it is worked out: a procedure calling itself is taken to return no rows
        var shape = _model.ModuleTree(name) is ProcedureStatementBody { MethodSpecifier: null } p ? Analyze(name, p) : null;
        _shapes[name] = shape;
        return shape;
    }

    private sealed record ResultSetSite(TSqlStatement Statement, int Count, bool InLoop, IReadOnlyList<ColumnInfo>? Columns, bool Cursors);

    private ProcedureShape Analyze(ObjectName name, ProcedureStatementBody p)
    {
        var scope = new Scope();
        var parameters = p.Parameters.Select(x => (Source: x.VariableName.Value.TrimStart('@'), Type: _model.TypeOf(x.DataType, name.Schema), x)).ToList();
        foreach (var x in parameters)
            scope.DefineVariable(x.Source, x.Type);

        var sites = new List<ResultSetSite>();
        var tempTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool returnsCode = false, commits = false;
        var frames = new List<(IList<TSqlStatement> List, int Index)>();
        void Walk(IList<TSqlStatement?> statements, bool inLoop)
        {
            var list = statements.Where(x => x is not null).Cast<TSqlStatement>().ToList();
            frames.Add((list, 0));
            for (int k = 0; k < list.Count; k++)
            {
                frames[^1] = (list, k);
                var s = list[k];
                switch (s)
                {
                    case null:
                        break;
                    case DeclareVariableStatement d:
                        foreach (var e in d.Declarations)
                            if (!scope.TryVariable(e.VariableName.Value, out _) && e.DataType is not null)
                                scope.DefineVariable(e.VariableName.Value, _model.TypeOf(e.DataType, name.Schema));
                        break;
                    case DeclareTableVariableStatement t:
                        scope.DefineTable(t.Body.VariableName.Value, new RelationInfo(null, ObjectKind.Table,
                            t.Body.Definition.ColumnDefinitions.Select(col => new ColumnInfo(col.ColumnIdentifier.Value,
                                col.DataType is null ? null : _model.TypeOf(col.DataType, name.Schema))).ToList()));
                        break;
                    case CreateTableStatement create when IsTemp(create.SchemaObjectName):
                        tempTables.Add(create.SchemaObjectName.BaseIdentifier.Value);
                        scope.DefineTable(create.SchemaObjectName.BaseIdentifier.Value, new RelationInfo(null, ObjectKind.Table,
                            create.Definition.ColumnDefinitions.Select(col => new ColumnInfo(col.ColumnIdentifier.Value,
                                col.DataType is null ? null : _model.TypeOf(col.DataType, name.Schema))).ToList()));
                        break;
                    case SelectStatement { Into: { } into } select when IsTemp(into):
                        tempTables.Add(into.BaseIdentifier.Value);
                        if (Columns(select, scope, name.Schema) is { } created)
                            scope.DefineTable(into.BaseIdentifier.Value, new RelationInfo(null, ObjectKind.Table, created));
                        break;
                    case SelectStatement select when IsResultSet(select):
                        sites.Add(new ResultSetSite(s, 1, inLoop, Columns(select, scope, name.Schema), false));
                        break;
                    case InsertStatement { InsertSpecification: { OutputClause: not null, OutputIntoClause: null } }
                        or UpdateStatement { UpdateSpecification: { OutputClause: not null, OutputIntoClause: null } }
                        or DeleteStatement { DeleteSpecification: { OutputClause: not null, OutputIntoClause: null } }
                        or MergeStatement { MergeSpecification: { OutputClause: not null, OutputIntoClause: null } }:
                        sites.Add(new ResultSetSite(s, 1, inLoop, null, false));
                        break;
                    case ExecuteStatement e when IsDynamic(e.ExecuteSpecification):
                        if (DynamicReturnsRows(e, frames))
                            sites.Add(new ResultSetSite(s, 1, inLoop, null, false));
                        break;
                    case ExecuteStatement e when CalleeName(e.ExecuteSpecification, name.Schema) is { } callee && Shape(callee) is { } shape:
                        commits |= shape.Commits;
                        if (shape.IsFunction)
                            sites.Add(new ResultSetSite(s, 1, inLoop, shape.Columns, false));
                        else if (shape.Cursors.Count > 0)
                            sites.Add(new ResultSetSite(s, shape.Cursors.Count, inLoop, null, true));
                        break;
                    case ReturnStatement { Expression: not null }:
                        returnsCode = true;
                        break;
                    case BeginTransactionStatement or CommitTransactionStatement or RollbackTransactionStatement:
                        commits = true;
                        break;
                    case BeginEndBlockStatement b:
                        Walk(b.StatementList.Statements, inLoop);
                        break;
                    case IfStatement i:
                        Walk([i.ThenStatement, i.ElseStatement], inLoop);
                        break;
                    case WhileStatement w:
                        Walk([w.Statement], true);
                        break;
                    case TryCatchStatement tc:
                        Walk(tc.TryStatements.Statements, inLoop);
                        Walk(tc.CatchStatements.Statements, inLoop);
                        break;
                }
            }
            frames.RemoveAt(frames.Count - 1);
        }
        Walk(p.StatementList?.Statements ?? [], false);

        bool function = _x.Options.ResultSetStyle == ResultSetStyle.Function
                        && sites is [{ Count: 1, InLoop: false, Cursors: false, Columns: { Count: > 0 } columns }]
                        && columns.All(col => col.Type is not null)
                        && !parameters.Any(x => x.x.Modifier == ParameterModifier.Output)
                        && !returnsCode && !commits;
        var resultColumns = function ? UniqueColumns(sites[0].Columns!) : [];
        var reserved = resultColumns.Select(col => _names.Alias(col.Name)).ToHashSet(StringComparer.Ordinal);

        var shapeParameters = parameters.Select(x =>
        {
            string pgName = _names.Alias(x.Source);
            if (reserved.Contains(pgName))
                pgName = "p_" + pgName;
            return new ProcedureParameterShape(x.Source, pgName, x.Type, _x.Types.Map(x.Type with { UserTypeName = null }, name).PgType,
                x.x.Modifier == ParameterModifier.Output, x.x.Value);
        }).ToList();
        var taken = shapeParameters.Select(x => x.PgName).ToHashSet(StringComparer.Ordinal);
        string Unique(string candidate)
        {
            while (!taken.Add(candidate))
                candidate += "_";
            return candidate;
        }

        var resultSets = new Dictionary<TSqlStatement, int>(ReferenceEqualityComparer.Instance);
        int total = 0;
        foreach (var site in sites)
        {
            resultSets[site.Statement] = total;
            total += site.Count;
        }
        var cursors = function ? [] : Enumerable.Range(1, total).Select(k => Unique(total == 1 ? "result_set" : $"result_set_{k}")).ToList();
        return new ProcedureShape(name, _names.Routine(name), function, shapeParameters)
        {
            ReturnCode = returnsCode ? Unique("return_code") : null,
            Cursors = cursors,
            Columns = resultColumns,
            ResultSets = resultSets,
            ResultSetCount = total,
            TempTables = tempTables,
            Commits = commits,
        };
    }

    /// <summary>A SELECT that returns rows to the caller (not assigning variables, not creating a table).</summary>
    private static bool IsResultSet(SelectStatement select) =>
        select.Into is null && !(select.QueryExpression is QuerySpecification spec && spec.SelectElements.OfType<SelectSetVariable>().Any());

    private IReadOnlyList<ColumnInfo>? Columns(SelectStatement select, Scope scope, string schema)
    {
        try
        {
            // FOR JSON returns one column of JSON text, whatever the select list.
            if (select.QueryExpression is QuerySpecification { ForClause: JsonForClause })
                return [new ColumnInfo(QueryTranslator.ForJsonColumn, new SqlServerType("nvarchar", -1))];
            var inner = scope.Child();
            _model.DefineCtes(select.WithCtesAndXmlNamespaces, inner, schema);
            return _model.OutputColumns(select.QueryExpression, inner, schema);
        }
        catch (UntranslatableException)
        {
            return null;
        }
    }

    /// <summary>Result columns with a name each (SQL Server allows none, and repeats) for RETURNS TABLE.</summary>
    private List<ColumnInfo> UniqueColumns(IReadOnlyList<ColumnInfo> columns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ColumnInfo>();
        for (int i = 0; i < columns.Count; i++)
        {
            string name = columns[i].Name.Length == 0 ? $"column{i + 1}" : columns[i].Name;
            string candidate = name;
            for (int n = 2; !used.Add(_names.Alias(candidate)); n++)
                candidate = $"{name}_{n}";
            result.Add(columns[i] with { Name = candidate });
        }
        return result;
    }

    /// <summary>The procedure an EXEC names, resolved in the source database (null for a string, a variable or an unknown name).</summary>
    private ObjectName? CalleeName(ExecuteSpecification spec, string? schema) =>
        spec.ExecutableEntity is ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } written } && spec.LinkedServer is null
            ? _model.ResolveRoutine(written, schema) is { } name && _model.Routine(name) is { Kind: ObjectKind.Procedure } ? name : null
            : null;

    /// <summary>The shape of the procedure an EXEC calls, or why it cannot be called.</summary>
    private ProcedureShape Callee(ExecuteSpecification spec, TSqlFragment source, ExpressionContext c)
    {
        if (spec.ExecutableEntity is not ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } written } reference)
            throw ExpressionTranslator.Untranslatable(source, "EXEC of a string or of a procedure named by a variable", DiagnosticCodes.ProceduralStatement);
        if (spec.LinkedServer is not null || reference.AdHocDataSource is not null)
            throw ExpressionTranslator.Untranslatable(source, "EXEC on a linked server", DiagnosticCodes.ExternalReference);
        if (CalleeName(spec, c.Schema) is { } name && Shape(name) is { } shape)
            return shape;
        string procedure = written.BaseIdentifier.Value;
        if (procedure.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
            throw ExpressionTranslator.Untranslatable(source, "sp_executesql (dynamic SQL)", DiagnosticCodes.ProceduralStatement);
        if (procedure.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || procedure.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))
            throw new UntranslatableException(DiagnosticCodes.SystemProcedure,
                $"{procedure} is a SQL Server system procedure, which PostgreSQL does not have: {ExpressionTranslator.Text(source).Trim()}");
        throw new UntranslatableException(DiagnosticCodes.UnknownProcedure,
            $"EXEC calls {ExpressionTranslator.Text(written)}, which is not a procedure of the source database: {ExpressionTranslator.Text(source).Trim()}");
    }

    /// <summary>The arguments of an EXEC by the callee's parameter number (positional first, then <c>@name = value</c>).</summary>
    private static Dictionary<int, ExecuteParameter> Arguments(ExecuteSpecification spec, ProcedureShape callee)
    {
        var result = new Dictionary<int, ExecuteParameter>();
        int position = 0;
        foreach (var a in ((ExecutableProcedureReference)spec.ExecutableEntity).Parameters)
        {
            int index = a.Variable is null
                ? position++
                : callee.Parameters.ToList().FindIndex(p => string.Equals(p.SourceName, a.Variable.Name.TrimStart('@'), StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index >= callee.Parameters.Count)
                throw ExpressionTranslator.Untranslatable(a, $"an argument {callee.Name} does not declare");
            result[index] = a;
        }
        return result;
    }

    private static bool IsDefault(ExecuteParameter? a) => a?.ParameterValue is null or DefaultLiteral;

    /// <summary>The rows of a procedure converted to a function: <c>SELECT * FROM f(name =&gt; value, …)</c>.</summary>
    private PgSelect CalleeRows(ExecuteSpecification spec, ProcedureShape callee, ExpressionContext c)
    {
        var given = Arguments(spec, callee);
        var args = new List<PgExpr>();
        for (int i = 0; i < callee.Parameters.Count; i++)
        {
            if (!given.TryGetValue(i, out var a) || IsDefault(a))
                continue;
            var p = callee.Parameters[i];
            args.Add(new PgTemplate($"{NameMapper.Quote(p.PgName)} => {{0}}", [_x.ConvertTo(a.ParameterValue, p.Type, c)]));
        }
        return new PgSelect { Targets = [PgTarget.All()], From = [new PgFunctionRef(callee.PgName.Qualified, args, null, [])] };
    }

    /// <summary>
    /// <c>EXEC [@rc =] p args</c> in a body. A procedure is CALLed with named arguments; its INOUT
    /// parameters need variables, so a caller's <c>@v OUTPUT</c> is passed as is and anything else
    /// goes through a helper variable. A procedure converted to a function returns rows: they are
    /// the caller's result set.
    /// </summary>
    private IEnumerable<PgPlStatement> Execute(ExecuteStatement e, BodyState st)
    {
        var c = st.C;
        var spec = e.ExecuteSpecification;
        if (IsDynamic(spec))
            return Dynamic(e, st);
        var callee = Callee(spec, e, c);
        if (callee.Commits && st.TryDepth > 0)
            throw ExpressionTranslator.Untranslatable(e,
                $"EXEC of {callee.Name}, which ends a transaction, inside TRY (PL/pgSQL cannot end a transaction inside a block with an exception handler)",
                DiagnosticCodes.TransactionInTry);
        if (callee.TempTables.FirstOrDefault(st.CreatedTempTables.Contains) is { } shared)
            c.Report(DiagnosticCodes.TempTableReplaced, Severity.Warning,
                $"{callee.Name} creates its own {shared}, as this routine does: in SQL Server each has its own, in PostgreSQL the callee's replaces this one's and is dropped when the callee ends.");
        var result = new List<PgPlStatement>();
        if (callee.IsFunction)
        {
            result.AddRange(ResultSet(e, () => CalleeRows(spec, callee, c), st));
        }
        else
        {
            IReadOnlyList<PgExpr>? cursors = null;
            if (callee.Cursors.Count > 0)
            {
                if (st.Signature.Shape is { IsFunction: false } own && own.ResultSets.TryGetValue(e, out int first))
                    cursors = own.Cursors.Skip(first).Take(callee.Cursors.Count).Select(cursor => (PgExpr)new PgColumnRef(cursor, st.Signature.RoutineName)).ToList();
                else
                    c.Report(DiagnosticCodes.ResultSetInBlock, Severity.Error,
                        $"{callee.Name} returns its rows through refcursor parameters, and they do not reach this block's caller: {ExpressionTranslator.Text(e).Trim()}");
            }
            result.AddRange(Call(spec, callee, cursors, st));
        }
        if (spec.Variable is { } status && (callee.IsFunction || callee.ReturnCode is null))
        {
            result.Add(new PgAssign(Target(status, st).Target, new PgLiteral("0")));
            st.Assigned.Add(status.Name.TrimStart('@'));
        }
        return result;
    }

    private List<PgPlStatement> Call(ExecuteSpecification spec, ProcedureShape callee, IReadOnlyList<PgExpr>? cursors, BodyState st)
    {
        var c = st.C;
        var given = Arguments(spec, callee);
        var before = new List<PgPlStatement>();
        var args = new List<PgCallArgument>();
        var helpers = new Dictionary<string, int>(StringComparer.Ordinal);
        PgColumnRef ArgumentHelper(string type)
        {
            int n = helpers.GetValueOrDefault(type);
            helpers[type] = n + 1;
            if (!st.ArgumentHelpers.TryGetValue(type, out var pool))
                st.ArgumentHelpers[type] = pool = [];
            if (n == pool.Count)
                pool.Add(Helper($"sql2pg_arg{st.ArgumentHelpers.Values.Sum(x => x.Count) + 1}", type, st).Name);
            return new PgColumnRef(pool[n]);
        }

        for (int i = 0; i < callee.Parameters.Count; i++)
        {
            var p = callee.Parameters[i];
            given.TryGetValue(i, out var a);
            if (!p.Output && a?.ParameterValue is VariableReference table && st.TableVariableTypes.TryGetValue(table.Name, out var tableType))
            {
                // PostgreSQL allows no subquery in a CALL argument: the array goes through a helper variable.
                var array = ArgumentHelper(_names.Type(tableType).Qualified + "[]");
                before.Add(new PgAssign(array, TableVariableArray(table.Name, tableType, st)));
                args.Add(new PgCallArgument(array, p.PgName));
                continue;
            }
            if (!p.Output)
            {
                if (!IsDefault(a))
                    args.Add(new PgCallArgument(_x.ConvertTo(a!.ParameterValue, p.Type, c), p.PgName));
                continue;
            }
            if (a is { IsOutput: true, ParameterValue: VariableReference v })
            {
                args.Add(new PgCallArgument(Target(v, st).Target, p.PgName));
                st.Assigned.Add(v.Name.TrimStart('@'));
                continue;
            }
            // An OUTPUT parameter whose value the caller does not take back: PL/pgSQL still needs a variable for it.
            var helper = ArgumentHelper(p.PgType);
            PgExpr value = !IsDefault(a) ? _x.ConvertTo(a!.ParameterValue, p.Type, c)
                : p.Default is { } d ? _x.ConvertTo(d, p.Type, c)
                : PgLiteral.Null;
            before.Add(new PgAssign(helper, value));
            args.Add(new PgCallArgument(helper, p.PgName));
        }
        if (callee.ReturnCode is { } returnCode)
        {
            PgExpr target;
            if (spec.Variable is { } status)
            {
                target = Target(status, st).Target;
                st.Assigned.Add(status.Name.TrimStart('@'));
            }
            else
            {
                target = ArgumentHelper("integer");
            }
            args.Add(new PgCallArgument(target, returnCode));
        }
        for (int k = 0; k < callee.Cursors.Count; k++)
        {
            PgExpr cursor;
            if (cursors is not null)
            {
                cursor = cursors[k];
            }
            else
            {
                // A fresh name each call: OPEN on a refcursor holding the name of an open cursor fails.
                cursor = ArgumentHelper("refcursor");
                before.Add(new PgAssign(cursor, PgLiteral.Null));
            }
            args.Add(new PgCallArgument(cursor, callee.Cursors[k]));
        }
        return [.. before, new PgCall(callee.PgName, args)];
    }

    /// <summary>
    /// An EXEC in a script that needs no variables (constant arguments, no OUTPUT, no status):
    /// <c>CALL p(…)</c>, or <c>SELECT * FROM f(…)</c> for a procedure converted to a function.
    /// </summary>
    public PgNode ScriptCall(ExecuteStatement e, ExpressionContext c)
    {
        var spec = e.ExecuteSpecification;
        var callee = Callee(spec, e, c);
        if (callee.IsFunction)
            return new PgQueryStatement(CalleeRows(spec, callee, c));
        var given = Arguments(spec, callee);
        var args = new List<PgCallArgument>();
        for (int i = 0; i < callee.Parameters.Count; i++)
            if (given.TryGetValue(i, out var a) && !IsDefault(a))
                args.Add(new PgCallArgument(_x.ConvertTo(a.ParameterValue, callee.Parameters[i].Type, c), callee.Parameters[i].PgName));
        if (callee.Cursors.Count > 0)
            c.Report(DiagnosticCodes.ResultSetCursor, Severity.Warning,
                $"{callee.Name} returns its rows through refcursor parameters: run the CALL and FETCH ALL from each cursor inside one transaction.");
        return new PgCall(callee.PgName, args);
    }

    /// <summary>Whether an EXEC can run as a plain statement in a script (no variables involved).</summary>
    /// <param name="parameters">Command text: a variable argument is a command parameter, so it counts as a value too.</param>
    public static bool IsPlainCall(ExecuteStatement e, bool parameters = false) =>
        !IsDynamic(e.ExecuteSpecification)
        && e.ExecuteSpecification is { Variable: null, LinkedServer: null, ExecutableEntity: ExecutableProcedureReference { ProcedureReference.ProcedureReference: not null } p }
        && p.Parameters.All(a => !a.IsOutput && (a.ParameterValue is Literal or UnaryExpression { Expression: Literal } || parameters && a.ParameterValue is VariableReference));

    /// <summary>INSERT … EXEC of a procedure converted to a function: <c>INSERT … SELECT * FROM f(…)</c>.</summary>
    private PgQuery? ExecuteSource(ExecuteInsertSource source, ExpressionContext c)
    {
        var callee = Callee(source.Execute, source, c);
        if (!callee.IsFunction)
            throw ExpressionTranslator.Untranslatable(source,
                $"INSERT … EXEC of {callee.Name}, which is not a set-returning function (it has OUTPUT parameters, a status code or transaction control, or not exactly one result set),",
                DiagnosticCodes.InsertExec);
        return CalleeRows(source.Execute, callee, c);
    }

    /// <summary>
    /// A statement returning rows to the caller. A procedure converted to a function returns them
    /// with RETURN QUERY (each column cast to the declared result type, which PostgreSQL requires
    /// exactly); a procedure opens its refcursor parameter for them.
    /// </summary>
    private IEnumerable<PgPlStatement> ResultSet(TSqlStatement s, Func<PgQuery> query, BodyState st)
    {
        switch (st.Signature)
        {
            case { Kind: BodyKind.AnonymousBlock }:
                return [Untranslated(s, DiagnosticCodes.ResultSetInBlock,
                    "This statement returns rows, and a DO block cannot: turn the batch into a function, or keep the rows in a temporary table and query it after the block.", st)];
            case { Kind: BodyKind.Procedure, Shape: { IsFunction: true } shape }:
                return [new PgReturnQuery(Typed(Limited(query(), st.RowLimit), shape))];
            case { Kind: BodyKind.Procedure, Shape: { } shape } when shape.ResultSets.TryGetValue(s, out int index) && index < shape.Cursors.Count:
                if (st.LoopDepth > 0)
                    throw ExpressionTranslator.Untranslatable(s, "A result set inside a loop (one cursor per result set)", DiagnosticCodes.ProceduralStatement);
                return [new PgOpenCursor(shape.Cursors[index]) { Query = Limited(query(), st.RowLimit) }];
            default:
                return [Untranslated(s, DiagnosticCodes.ProceduralStatement, "A result set returned by this routine is not translated yet.", st)];
        }
    }

    /// <summary>The rows of a query cast to the result types; a query whose columns cannot be cast in place is wrapped.</summary>
    private PgQuery Typed(PgQuery query, ProcedureShape shape)
    {
        var types = shape.Columns.Select(col => BaseType(_x.Types.Map(col.Type! with { UserTypeName = null }, shape.Name).PgType)).ToList();
        var names = shape.Columns.Select(col => _names.Alias(col.Name)).ToList();
        if (query is PgSelect { Distinct: false } select && select.Targets.Count == types.Count && select.Targets.All(t => !t.Star && t.Expression is not null))
            return select with { Targets = select.Targets.Select((t, i) => new PgTarget(new PgCast(t.Expression!, types[i]), names[i])).ToList() };
        var columns = Enumerable.Range(1, types.Count).Select(i => $"c{i}").ToList();
        return new PgSelect
        {
            Targets = columns.Select((col, i) => new PgTarget(new PgCast(new PgColumnRef(col, "sql2pg_q"), types[i]), names[i])).ToList(),
            From = [new PgSubqueryRef(query, "sql2pg_q", columns)],
        };
    }

    /// <summary>A type without its modifiers (a function's result types have none): <c>varchar(40)</c> is <c>varchar</c>, <c>char(5)</c> is <c>bpchar</c>.</summary>
    internal static string BaseType(string pgType)
    {
        string type = Regex.Replace(pgType, @"\(\s*\d+(\s*,\s*-?\d+)?\s*\)", "").Trim();
        return type is "char" or "character" ? "bpchar" : type;
    }

    /// <summary>The query limited to the SET ROWCOUNT in force.</summary>
    private static PgQuery Limited(PgQuery query, int? limit)
    {
        if (limit is null)
            return query;
        var n = new PgLiteral(limit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return query switch
        {
            PgSelect { Limit: null, Offset: null } s => s with { Limit = n },
            PgSetOperation { Limit: null, Offset: null } u => u with { Limit = n },
            _ => new PgSelect { Targets = [PgTarget.All()], From = [new PgSubqueryRef(query, "sql2pg_q", [])], Limit = n },
        };
    }

    // ---- transactions ----

    private IEnumerable<PgPlStatement> Transaction(TransactionStatement t, BodyState st)
    {
        var c = st.C;
        switch (t)
        {
            case BeginTransactionStatement { Distributed: true }:
                throw ExpressionTranslator.Untranslatable(t, "BEGIN DISTRIBUTED TRANSACTION", DiagnosticCodes.ProceduralStatement);
            case BeginTransactionStatement begin:
                if (begin.Name?.Value is { } named)
                    st.TransactionNames.Add(named);
                st.TransactionBegun = true;
                if (st.Signature.Kind == BodyKind.Trigger)
                {
                    c.Report(DiagnosticCodes.BeginTransactionDropped, Severity.Info,
                        "BEGIN TRANSACTION in a trigger is dropped: the trigger runs inside the triggering statement's transaction.");
                    return [];
                }
                if (st.TryDepth > 0)
                {
                    c.Report(DiagnosticCodes.BeginTransactionDropped, Severity.Info,
                        "BEGIN TRANSACTION in TRY: the work before the TRY block is committed, and the TRY's work is one transaction with what follows it.");
                    return [];
                }
                c.Report(DiagnosticCodes.BeginTransactionDropped, Severity.Info,
                    "BEGIN TRANSACTION becomes COMMIT: the work before it is committed, as SQL Server's autocommit had done, and a new transaction starts.");
                return [new PgTransactionControl(true)];
            case RollbackTransactionStatement when st.Signature.Kind == BodyKind.Trigger:
                return [TriggerRollback()];
            case CommitTransactionStatement when st.Signature.Kind == BodyKind.Trigger:
                throw ExpressionTranslator.Untranslatable(t, "COMMIT inside a trigger (it runs inside the triggering statement's transaction)", DiagnosticCodes.ProceduralStatement);
            case CommitTransactionStatement or RollbackTransactionStatement when st.TryDepth > 0:
                throw ExpressionTranslator.Untranslatable(t,
                    "COMMIT or ROLLBACK inside TRY, other than a COMMIT ending it (PL/pgSQL cannot end a transaction inside a block with an exception handler)",
                    DiagnosticCodes.TransactionInTry);
            case CommitTransactionStatement:
                return [new PgTransactionControl(true)];
            case RollbackTransactionStatement rollback when rollback.Name?.Value is not { } rolledBack || st.TransactionNames.Contains(rolledBack):
                if (st.TableVariableSetup.Count > 0)
                    c.Report(DiagnosticCodes.TableVariableRollback, Severity.Warning,
                        $"ROLLBACK with table variables ({string.Join(", ", st.TableVariableSetup.Keys)}): SQL Server keeps their rows, PostgreSQL rolls them back (they are created again, empty).");
                return [new PgTransactionControl(false), .. RecreateTableVariables(st)];
            case RollbackTransactionStatement:
                throw ExpressionTranslator.Untranslatable(t, "ROLLBACK to a savepoint (PL/pgSQL has none; a BEGIN … EXCEPTION block is the nearest)", DiagnosticCodes.Savepoint);
            case SaveTransactionStatement:
                throw ExpressionTranslator.Untranslatable(t, "SAVE TRANSACTION (PL/pgSQL has no savepoints; a BEGIN … EXCEPTION block is the nearest)", DiagnosticCodes.Savepoint);
            default:
                throw ExpressionTranslator.Untranslatable(t, "this transaction statement", DiagnosticCodes.ProceduralStatement);
        }
    }
}
