using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Dynamic SQL: <c>EXEC (string)</c> and <c>sp_executesql</c>. The string is put together from
/// its literal pieces, the pieces that are not literals stand in as markers, and the result is
/// parsed and translated as T-SQL. It runs with <c>EXECUTE</c>: sp_executesql's parameters become
/// <c>$1</c>, <c>$2</c>, … with <c>USING</c>; a <c>QUOTENAME(x)</c> piece is a name (<c>%I</c> in
/// <c>format()</c>, mapped as the converter maps names); a piece inside quotes is a value
/// (<c>%L</c>); any other piece is spliced in as it is (<c>%s</c>). A string held in a variable is
/// followed back to the SET or DECLARE that last gave it its value.
/// </summary>
public sealed partial class ProceduralTranslator
{
    private const string MarkerPrefix = "sql2pg_m";

    /// <summary>The markers of the current dynamic SQL that stand for names used as they are (role names).</summary>
    private readonly HashSet<string> _verbatimMarkers = new(StringComparer.Ordinal);

    /// <summary>The dynamic SQL of an EXEC, ready to translate: the template T-SQL and what each marker stands for.</summary>
    private sealed record DynamicTemplate(string Sql, IReadOnlyList<PgFormatArgument> Arguments, bool SplicesText)
    {
        /// <summary>The markers of pieces that can only be numbers (CAST of a number, STR()).</summary>
        public IReadOnlyList<string> NumberMarkers { get; init; } = [];

        /// <summary>
        /// The template with each number marker as a number, for where T-SQL takes only a number
        /// (<c>RESTART WITH 42</c>): the marker becomes <c>91000000000000000n7</c>, which the
        /// writer replaces like any marker.
        /// </summary>
        public DynamicTemplate WithNumberMarkers()
        {
            string sql = Sql;
            var arguments = Arguments.ToList();
            for (int i = 0; i < arguments.Count; i++)
            {
                if (!NumberMarkers.Contains(arguments[i].Marker))
                    continue;
                string number = $"91{i + 1:D16}7";
                sql = sql.Replace(arguments[i].Marker, number, StringComparison.Ordinal);
                arguments[i] = arguments[i] with { Marker = number };
            }
            return new DynamicTemplate(sql, arguments, SplicesText);
        }
    }

    /// <summary>sp_executesql's declared parameters, in order, with the caller's values.</summary>
    private sealed record DynamicParameter(string Name, SqlServerType Type, DataTypeReference DataType, bool Output, ExecuteParameter? Argument);

    /// <summary>Whether an EXEC runs dynamic SQL (a string, or sp_executesql).</summary>
    private static bool IsDynamic(ExecuteSpecification spec) => DynamicSqlText.IsDynamic(spec);

    private static UntranslatableException DynamicUntranslatable(TSqlFragment source, string why) =>
        new(DiagnosticCodes.DynamicSqlUntranslatable, $"Dynamic SQL {why}: {ExpressionTranslator.Text(source).Trim()}");

    /// <summary>
    /// The template: literal pieces as they are, QUOTENAME pieces as marker names, pieces between
    /// quotes as marker values, others as spliced markers. <paramref name="argument"/> translates a
    /// piece in the caller's context.
    /// </summary>
    private DynamicTemplate Template(ScalarExpression text, IReadOnlyList<(IList<TSqlStatement> List, int Index)> at, TSqlFragment source, ExpressionContext c)
    {
        PgExpr argument(ScalarExpression piece) => _x.Text(piece, c);
        var pieces = new List<ScalarExpression>();
        DynamicSqlText.Pieces(text, pieces);
        var sql = new System.Text.StringBuilder();
        var arguments = new List<PgFormatArgument>();
        var numbers = new List<string>();
        bool splices = false;
        bool stripQuote = false;
        foreach (var piece in pieces)
        {
            // A piece that is a constant where the text is put together (a variable set to a
            // literal, NCHAR(13), QUOTENAME(N'x')) is part of the text, as in SQL Server.
            string? constant = piece is StringLiteral literal ? literal.Value
                : DynamicSqlText.TryFold(piece, at, out var folded) ? folded
                : null;
            if (constant is not null)
            {
                string value = constant;
                if (stripQuote)
                {
                    if (!value.StartsWith('\''))
                        throw DynamicUntranslatable(source, "with a value spliced into the middle of a string literal");
                    value = value[1..];
                    stripQuote = false;
                }
                sql.Append(value);
                continue;
            }
            if (stripQuote)
                throw DynamicUntranslatable(source, "with two values spliced into one string literal");
            string marker = $"{MarkerPrefix}{arguments.Count + 1}_";
            bool insideString = sql.ToString().Count(ch => ch == '\'') % 2 == 1;
            if (piece is FunctionCall { CallTarget: null, FunctionName.Value: var fn } quote && fn.Equals("QUOTENAME", StringComparison.OrdinalIgnoreCase)
                && quote.Parameters.Count >= 1 && !insideString)
            {
                arguments.Add(new PgFormatArgument(marker, 'I', argument(quote.Parameters[0])));
                sql.Append('[').Append(marker).Append(']');
            }
            else if (insideString)
            {
                // '… = ''' + @v + '''': the value, quoted by format().
                sql.Length--;
                stripQuote = true;
                arguments.Add(new PgFormatArgument(marker, 'L', argument(piece)));
                sql.Append(marker);
            }
            else
            {
                arguments.Add(new PgFormatArgument(marker, 's', argument(piece)));
                bool number = IsNumberText(piece, c);
                splices |= !number;
                if (number)
                    numbers.Add(marker);
                sql.Append(marker);
            }
        }
        if (stripQuote)
            throw DynamicUntranslatable(source, "with a value spliced into an unterminated string literal");
        return new DynamicTemplate(sql.ToString(), arguments, splices) { NumberMarkers = numbers };
    }

    /// <summary>A piece that can only be a number as text: CAST/CONVERT of a number, or STR().</summary>
    private bool IsNumberText(ScalarExpression piece, ExpressionContext c) => piece switch
    {
        CastCall cast => TypeRules.IsNumeric(_x.TypeOf(cast.Parameter, c)),
        ConvertCall convert => TypeRules.IsNumeric(_x.TypeOf(convert.Parameter, c)),
        FunctionCall { CallTarget: null, FunctionName.Value: var name } when name.Equals("STR", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    // ---- sp_executesql ----

    private List<DynamicParameter> DynamicParameters(ExecuteSpecification spec, TSqlFragment source, string? schema)
    {
        if (spec.ExecutableEntity is not ExecutableProcedureReference p || p.Parameters.Count < 2)
            return [];
        if (p.Parameters[1].ParameterValue is not StringLiteral { Value: var definitions })
            throw DynamicUntranslatable(source, "whose parameter list is not a literal");
        string declare = Regex.Replace(definitions, @"\s+OUT(PUT)?\b", "", RegexOptions.IgnoreCase);
        var parsed = new TSql160Parser(true).Parse(new StringReader("DECLARE " + declare), out var errors);
        if (errors.Count > 0 || parsed is not TSqlScript { Batches: [{ Statements: [DeclareVariableStatement d] }] })
            throw DynamicUntranslatable(source, "whose parameter list cannot be read");
        var arguments = p.Parameters.Skip(2).ToList();
        var result = new List<DynamicParameter>();
        for (int i = 0; i < d.Declarations.Count; i++)
        {
            var x = d.Declarations[i];
            string name = x.VariableName.Value;
            bool output = Regex.IsMatch(definitions, Regex.Escape(name) + @"\b[^@]*?\bOUT(PUT)?\b", RegexOptions.IgnoreCase);
            var argument = arguments.FirstOrDefault(a => a.Variable?.Name.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
                           ?? (i < arguments.Count && arguments[i].Variable is null ? arguments[i] : null);
            result.Add(new DynamicParameter(name, _model.TypeOf(x.DataType, schema), x.DataType, output, argument));
        }
        return result;
    }

    // ---- translation ----

    /// <summary>The translated statements of the dynamic SQL, the INTO targets of a SELECT assigning OUTPUT parameters, and whether it returns rows.</summary>
    private sealed record DynamicStatements(IReadOnlyList<PgStatement> Statements, IReadOnlyList<PgExpr> Into, bool ReturnsRows);

    private DynamicStatements TranslateDynamic(ref DynamicTemplate template, IReadOnlyList<DynamicParameter> parameters, TSqlFragment source, BodyState st)
    {
        var fragment = new TSql160Parser(true).Parse(new StringReader(template.Sql), out var errors);
        if (errors.Count > 0 && template.NumberMarkers.Count > 0)
        {
            var numbered = template.WithNumberMarkers();
            var retry = new TSql160Parser(true).Parse(new StringReader(numbered.Sql), out var retryErrors);
            if (retryErrors.Count == 0)
                (template, fragment, errors) = (numbered, retry, retryErrors);
        }
        if (errors.Count > 0)
            throw DynamicUntranslatable(source, $"that does not parse as T-SQL ({errors[0].Message})");
        var statements = ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
            throw DynamicUntranslatable(source, "with no statement");

        // The dynamic batch sees its parameters, not the caller's variables.
        var scope = new Scope();
        foreach (var p in parameters)
            scope.DefineVariable(p.Name, p.Type);
        var inner = new ExpressionContext(scope, st.C.Schema, st.C.Owner);
        var saved = (_x.VariableWriter, _x.ProceduralGlobal, _x.ProceduralFunction, _queries.TableVariable, _queries.CursorVariable, _queries.TableParameter);
        _x.VariableWriter = name => parameters.ToList().FindIndex(p => p.Name.TrimStart('@').Equals(name.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) is var k and >= 0
            ? new PgKeyword("$" + (k + 1))
            : throw DynamicUntranslatable(source, $"reading {name}, which the dynamic SQL does not declare");
        _x.ProceduralGlobal = null;
        _x.ProceduralFunction = null;
        _queries.TableVariable = null;
        _queries.TableParameter = null;
        _queries.CursorVariable = null;
        try
        {
            var translated = new List<PgStatement>();
            var into = new List<PgExpr>();
            bool rows = false;
            foreach (var s in statements)
            {
                switch (s)
                {
                    case SelectStatement { Into: null, QueryExpression: QuerySpecification spec } select
                        when spec.SelectElements.Count > 0 && spec.SelectElements.All(x => x is SelectSetVariable):
                        if (statements.Count > 1)
                            throw DynamicUntranslatable(source, "assigning OUTPUT parameters alongside other statements");
                        var sets = spec.SelectElements.Cast<SelectSetVariable>().ToList();
                        foreach (var set in sets)
                        {
                            var parameter = parameters.FirstOrDefault(p => p.Name.Equals(set.Variable.Name, StringComparison.OrdinalIgnoreCase));
                            if (parameter is not { Output: true, Argument: { IsOutput: true, ParameterValue: VariableReference target } })
                                throw DynamicUntranslatable(source, $"assigning {set.Variable.Name}, which is not an OUTPUT parameter passed a variable,");
                            into.Add(Target(target, st).Target);
                            st.Assigned.Add(target.Name.TrimStart('@'));
                        }
                        if (!(spec.GroupByClause is null && sets.Any(x => HasAggregate(x.Expression))) && !AtMostOneRow(spec, inner))
                            st.C.Report(DiagnosticCodes.SelectAssignmentOrder, Severity.Warning,
                                "Dynamic SELECT assigning OUTPUT parameters may return several rows: SQL Server keeps the last row's values, PostgreSQL's EXECUTE … INTO the first's.");
                        translated.Add(new PgQueryStatement(_queries.Query(Clone(spec, sets.Select(x => x.Expression).ToList(), alias: false), inner, select.WithCtesAndXmlNamespaces)));
                        break;
                    case SelectStatement select:
                        rows |= select.Into is null;
                        translated.Add(_queries.Statement(select, inner));
                        break;
                    case InsertStatement or UpdateStatement or DeleteStatement or MergeStatement or TruncateTableStatement:
                        translated.Add(_queries.Statement(s, inner));
                        break;
                    case PredicateSetStatement or SetCommandStatement:
                        SessionOptions.ReportDropped(s, inner);
                        break;
                    case ProcedureStatementBody or FunctionStatementBody or TriggerStatementBody:
                        translated.AddRange(CreatedModuleStatements(s, template, parameters, source));
                        break;
                    default:
                        // DDL a routine may run (WP 4.1), translated as it is when written in the routine itself.
                        if (RoutineDdl(s, st) is { } ddl && ddl.All(x => x is PgSqlStatement))
                        {
                            translated.AddRange(ddl.Cast<PgSqlStatement>().Select(x => x.Statement));
                            break;
                        }
                        throw DynamicUntranslatable(source, $"containing {s.GetType().Name.Replace("Statement", "")} (only queries, DML, routine and trigger definitions, and the DDL a routine may run are translated)");
                }
            }
            if (rows && statements.Count > 1)
                throw DynamicUntranslatable(source, "returning rows alongside other statements");
            return new DynamicStatements(translated, into, rows);
        }
        finally
        {
            (_x.VariableWriter, _x.ProceduralGlobal, _x.ProceduralFunction, _queries.TableVariable, _queries.CursorVariable, _queries.TableParameter) = saved;
            // The markers stand for names and values not known until run time: no warnings about them.
            st.C.Diagnostics.AddRange(inner.Diagnostics.Where(d => !d.Message.Contains(MarkerPrefix, StringComparison.Ordinal)));
            st.C.CompatFunctions.UnionWith(inner.CompatFunctions);
            st.C.Extensions.UnionWith(inner.Extensions);
        }
    }

    /// <summary>
    /// Translates a procedure, function or trigger a procedure creates with dynamic SQL, given the
    /// name it creates and its CREATE statement; null when it is not one the semantic model knows
    /// with that definition. Set by the schema translator.
    /// </summary>
    public Func<ObjectName, TSqlStatement, Schema.TranslatedObject?>? CreatedModule { get; set; }

    /// <summary>
    /// <c>EXEC (N'CREATE PROCEDURE …')</c>: the routine or trigger it creates, translated as if it
    /// were one of the database's own, its statements run by EXECUTE. The whole text must be known
    /// (no names or values put in at run time), as the translation of the body depends on it.
    /// </summary>
    private IEnumerable<PgStatement> CreatedModuleStatements(TSqlStatement s, DynamicTemplate template, IReadOnlyList<DynamicParameter> parameters, TSqlFragment source)
    {
        var written = DynamicSqlText.CreatedName(s)!;
        string what = s switch { ProcedureStatementBody => "a procedure", FunctionStatementBody => "a function", _ => "a trigger" };
        if (template.Arguments.Count > 0 || parameters.Count > 0)
            throw DynamicUntranslatable(source, $"creating {what} from names or values put in at run time");
        var name = new ObjectName(written.SchemaIdentifier?.Value ?? SemanticModel.DefaultSchema, written.BaseIdentifier.Value);
        var created = CreatedModule?.Invoke(name, s)
                      ?? throw DynamicUntranslatable(source, $"creating {what} ({name}) that another statement creates with a different definition");
        if (created.Status == ObjectStatus.Manual)
        {
            var error = created.Diagnostics.First(d => d.Severity == Severity.Error);
            throw new UntranslatableException(error.Code, $"Dynamic SQL creating {name}, which cannot be translated: {error.Message}");
        }
        bool replace = s is CreateOrAlterProcedureStatement or CreateOrAlterFunctionStatement or CreateOrAlterTriggerStatement
            or AlterProcedureStatement or AlterFunctionStatement or AlterTriggerStatement;
        return created.Statements.Select(x => replace && x is PgCreateRoutine r ? r with { OrReplace = true } : x);
    }

    /// <summary>A name built at run time, mapped as the converter maps names: schemas through the schema map, then the name style.</summary>
    private PgExpr RuntimeName(PgExpr name, bool schema, TSqlFragment source)
    {
        PgExpr styled = _x.Options.NameStyle switch
        {
            NameStyle.Lower => new PgFunctionCall("lower", [name]),
            NameStyle.Preserve => name,
            _ => throw DynamicUntranslatable(source, "naming an object at run time under NameStyle SnakeCase (not mapped at run time)"),
        };
        if (!schema || _x.Options.SchemaMap.Count == 0)
            return styled;
        var map = new System.Text.StringBuilder("CASE lower({0})");
        var args = new List<PgExpr> { name };
        foreach (var (from, to) in _x.Options.SchemaMap)
            map.Append($" WHEN {PgLiteral.String(from.ToLowerInvariant()).Sql} THEN {PgLiteral.String(to).Sql}");
        map.Append(" ELSE {1} END");
        args.Add(styled);
        return new PgTemplate(map.ToString(), args);
    }

    /// <summary>EXEC (string) or sp_executesql in a body.</summary>
    private IEnumerable<PgPlStatement> Dynamic(ExecuteStatement e, BodyState st)
    {
        var spec = e.ExecuteSpecification;
        if (spec.Variable is not null)
            throw DynamicUntranslatable(e, "with a status code (EXEC @rc = sp_executesql …)");
        if (spec.ExecuteContext is { } context)
            st.C.Report(DiagnosticCodes.DynamicSqlContext, Severity.Warning,
                $"EXEC (…) AS {ExpressionTranslator.Text(context).Trim()} runs the SQL as that user in SQL Server; the converted EXECUTE runs it as the caller, with the caller's privileges. " +
                "To run it as a role, put SET ROLE before it and RESET ROLE after (the caller must be a member of the role).");
        var (text, at) = DynamicSqlText.Text(e, st.Frames.Select(f => (f.List, f.Index)).ToList())
                         ?? throw DynamicUntranslatable(e, "whose text cannot be followed back to a SET or DECLARE in this code");
        var parameters = DynamicParameters(spec, e, st.C.Schema);
        var template = Template(text, at, e, st.C);
        _verbatimMarkers.Clear();
        if (template.SplicesText)
            st.C.Report(DiagnosticCodes.DynamicSqlSpliced, Severity.Warning,
                "Dynamic SQL splices text in at run time (%s): it is used as it stands, so T-SQL syntax in it stays T-SQL.");
        var translated = TranslateDynamic(ref template, parameters, e, st);

        // Each name marker followed by '.' is a schema. Role names are used as they are.
        var arguments = template.Arguments.Select(a => a.Spec == 'I' && !_verbatimMarkers.Contains(a.Marker)
            ? a with { Value = RuntimeName(a.Value, IsSchemaMarker(template.Sql, a.Marker), e) }
            : a).ToList();
        if (template.Arguments.Any(a => a.Spec == 'I' && !_verbatimMarkers.Contains(a.Marker)))
            st.C.Report(DiagnosticCodes.DynamicIdentifier, Severity.Info,
                "A name in dynamic SQL comes from a variable: it is mapped at run time (lower case, and the schema map), so it must name an object as converted.");
        var command = new PgDynamicSql(translated.Statements, arguments) { TargetVersion = _x.Options.TargetVersion };
        var usingArgs = parameters.Select(p => p.Argument?.ParameterValue is { } value && value is not DefaultLiteral
            ? _x.ConvertTo(value, p.Type, st.C)
            : new PgCast(PgLiteral.Null, _x.Types.Map(p.Type with { UserTypeName = null }, st.C.Owner).PgType)).ToList();

        if (translated.ReturnsRows)
            return DynamicResultSet(e, command, usingArgs, st);
        // Several statements (a trigger is a function and a trigger) run one EXECUTE each: a
        // command with parameters ($1) cannot hold several.
        if (translated.Statements.Count > 1 && translated.Into.Count == 0)
            return translated.Statements.Select(s => new PgExecuteDynamic(command with { Statements = [s] }) { Using = usingArgs }).ToList();
        return [new PgExecuteDynamic(command) { Into = translated.Into, Using = usingArgs }];
    }

    private static bool IsSchemaMarker(string sql, string marker)
    {
        int at = sql.IndexOf(marker + "]", StringComparison.Ordinal);
        return at >= 0 && at + marker.Length + 1 < sql.Length && sql[at + marker.Length + 1] == '.';
    }

    /// <summary>Rows returned by dynamic SQL: a procedure opens its cursor parameter with EXECUTE.</summary>
    private IEnumerable<PgPlStatement> DynamicResultSet(ExecuteStatement e, PgDynamicSql command, IReadOnlyList<PgExpr> usingArgs, BodyState st)
    {
        if (st.Signature is { Kind: BodyKind.Procedure, Shape: { IsFunction: false } shape } && shape.ResultSets.TryGetValue(e, out int index) && index < shape.Cursors.Count)
        {
            if (st.LoopDepth > 0)
                throw ExpressionTranslator.Untranslatable(e, "A result set inside a loop (one cursor per result set)", DiagnosticCodes.ProceduralStatement);
            return [new PgOpenCursor(shape.Cursors[index]) { Execute = command, Using = usingArgs }];
        }
        return [Untranslated(e, st.Signature.Kind == BodyKind.AnonymousBlock ? DiagnosticCodes.ResultSetInBlock : DiagnosticCodes.ProceduralStatement,
            "Rows returned by dynamic SQL reach the caller only from a procedure, through a refcursor.", st)];
    }

    /// <summary>For the procedure's shape: whether dynamic SQL at this point returns rows (its text known and parsed).</summary>
    private static bool DynamicReturnsRows(ExecuteStatement e, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames)
    {
        if (!IsDynamic(e.ExecuteSpecification) || DynamicSqlText.Text(e, frames) is not var (text, at))
            return false;
        var pieces = new List<ScalarExpression>();
        DynamicSqlText.Pieces(text, pieces);
        var sql = new System.Text.StringBuilder();
        foreach (var piece in pieces)
            sql.Append(piece is StringLiteral literal ? literal.Value : DynamicSqlText.TryFold(piece, at, out var folded) && folded is not null ? folded : "[x]");
        var fragment = new TSql160Parser(true).Parse(new StringReader(sql.ToString()), out var errors);
        return errors.Count == 0 && ((TSqlScript)fragment).Batches.SelectMany(b => b.Statements)
            .Any(s => s is SelectStatement { Into: null } select && IsResultSet(select));
    }
}
