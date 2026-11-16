using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>User-defined functions: scalar, inline table-valued and multi-statement table-valued.</summary>
public sealed partial class SchemaTranslator
{
    /// <summary>
    /// A function as CREATE FUNCTION. A scalar function whose body is one RETURN is a
    /// <c>LANGUAGE sql</c> function with a SQL-standard <c>RETURN</c> body; other scalar functions
    /// are PL/pgSQL. An inline TVF is <c>RETURNS TABLE … LANGUAGE sql</c> over its query; a
    /// multi-statement TVF fills a temporary table and returns its rows. Volatility comes from what
    /// the body reads and calls.
    /// </summary>
    private TranslatedObject Function(Module m, SemanticModel model, ProceduralTranslator procedural)
    {
        var name = _names.Routine(m.Name);
        var dependsOn = model.Dependencies.Where(d => ObjectNameComparer.Instance.Equals(d.From, m.Name)).Select(d => d.To).ToList();
        if (model.ModuleTree(m.Name) is not FunctionStatementBody f)
            return Untranslated(m, name.Qualified, dependsOn, DiagnosticCodes.ParseError, $"The definition of {m.Name} could not be parsed.");
        if (f.MethodSpecifier is not null)
            return Untranslated(m, name.Qualified, dependsOn, DiagnosticCodes.ClrObject, $"{m.Name} is a CLR function: .NET code, not T-SQL.");

        var context = new ExpressionContext(new Scope(), m.Name.Schema, m.Name);
        try
        {
            // A parameter cannot share a name with a RETURNS TABLE column (both are output names).
            var resultNames = (f.ReturnType switch
            {
                SelectFunctionReturnType => model.Routine(m.Name)?.ResultColumns?.Select(col => col.Name) ?? [],
                TableValuedFunctionReturnType t => t.DeclareTableVariableBody.Definition.ColumnDefinitions.Select(col => col.ColumnIdentifier.Value),
                _ => [],
            }).Select(_names.Alias).ToHashSet(StringComparer.Ordinal);
            var (parameters, bodyParameters) = Parameters(f.Parameters, model, procedural, context, resultNames);
            PgCreateRoutine routine;
            switch (f.ReturnType)
            {
                case ScalarFunctionReturnType scalar:
                {
                    var type = model.TypeOf(scalar.DataType, m.Name.Schema);
                    var signature = new BodySignature(BodyKind.ScalarFunction, name.Name, bodyParameters, type, scalar.DataType);
                    PgRoutineBody body = SingleReturn(f.StatementList?.Statements ?? []) is { } value
                        ? new PgSqlReturnBody(procedural.ReturnExpression(value, signature, context))
                        : procedural.Body(f.StatementList?.Statements ?? [], signature, context);
                    routine = new PgCreateRoutine(name, false, parameters, body) { Returns = MapType(type, context) };
                    break;
                }
                case SelectFunctionReturnType inline:
                {
                    var columns = model.Routine(m.Name)?.ResultColumns ?? [];
                    var unknown = columns.FirstOrDefault(col => col.Type is null);
                    if (unknown is not null || columns.Count == 0)
                        throw new UntranslatableException(DiagnosticCodes.FunctionColumnTypeUnknown,
                            $"The type of column {unknown?.Name ?? "(none)"} of {m.Name}'s result is not known, so RETURNS TABLE cannot be written.");
                    var signature = new BodySignature(BodyKind.ScalarFunction, name.Name, bodyParameters);
                    var query = procedural.Query(inline.SelectStatement, signature, context);
                    routine = new PgCreateRoutine(name, false, parameters, new PgSqlBody([new PgQueryStatement(query)]))
                    {
                        ReturnsTable = columns.Select(col => new PgField(_names.Alias(col.Name), MapType(col.Type!, context))).ToList(),
                    };
                    break;
                }
                case TableValuedFunctionReturnType table:
                {
                    var definition = table.DeclareTableVariableBody.Definition;
                    var signature = new BodySignature(BodyKind.TableFunction, name.Name, bodyParameters) { ReturnTable = table.DeclareTableVariableBody };
                    var body = procedural.Body(f.StatementList?.Statements ?? [], signature, context);
                    routine = new PgCreateRoutine(name, false, parameters, body)
                    {
                        ReturnsTable = definition.ColumnDefinitions.Select(col => new PgField(_names.Alias(col.ColumnIdentifier.Value),
                            MapType(model.TypeOf(col.DataType, m.Name.Schema), context))).ToList(),
                    };
                    break;
                }
                default:
                    throw new UntranslatableException(DiagnosticCodes.UnsupportedExpression, $"{m.Name} returns something other than a value or a table.");
            }

            routine = routine with { Volatility = Volatility(m.Name, model, context.CompatFunctions) };
            return new TranslatedObject(m.Name, ObjectKind.Function, name.Qualified, Phase.Code, [routine], context.Diagnostics, dependsOn)
            {
                Extensions = context.Extensions.ToList(),
                CompatFunctions = context.CompatFunctions.ToList(),
            };
        }
        catch (UntranslatableException ex)
        {
            return Untranslated(m, name.Qualified, dependsOn, ex.Code, ex.Message, context.Diagnostics);
        }
    }

    /// <summary>
    /// A procedure as CREATE PROCEDURE, its result sets returned through INOUT refcursor parameters
    /// and its status code through INOUT return_code; or, when it returns one result set and nothing
    /// else (and the options ask for it), as a set-returning function.
    /// </summary>
    private TranslatedObject Procedure(Module m, SemanticModel model, ProceduralTranslator procedural)
    {
        var name = _names.Routine(m.Name);
        var dependsOn = model.Dependencies.Where(d => ObjectNameComparer.Instance.Equals(d.From, m.Name)).Select(d => d.To).ToList();
        if (model.ModuleTree(m.Name) is not ProcedureStatementBody p)
            return Untranslated(m, name.Qualified, dependsOn, DiagnosticCodes.ParseError, $"The definition of {m.Name} could not be parsed.");
        if (p.MethodSpecifier is not null)
            return Untranslated(m, name.Qualified, dependsOn, DiagnosticCodes.ClrObject, $"{m.Name} is a CLR procedure: .NET code, not T-SQL.");
        var shape = procedural.Shape(m.Name)
                    ?? throw new InvalidOperationException($"{m.Name} has no procedure shape.");

        var context = new ExpressionContext(new Scope(), m.Name.Schema, m.Name);
        try
        {
            var reserved = shape.Columns.Select(col => _names.Alias(col.Name)).ToHashSet(StringComparer.Ordinal);
            var (parameters, bodyParameters) = Parameters(p.Parameters, model, procedural, context, reserved);
            if (shape.ReturnCode is { } returnCode)
            {
                parameters.Add(new PgParameter(returnCode, "integer", "INOUT", PgLiteral.Null));
                context.Report(DiagnosticCodes.ReturnCodeParameter, Severity.Info,
                    $"{m.Name} returns a status code: it comes back in the INOUT parameter {returnCode} (0 when RETURN gives none).");
            }
            foreach (string cursor in shape.Cursors)
                parameters.Add(new PgParameter(cursor, "refcursor", "INOUT", PgLiteral.Null));
            if (shape.Cursors.Count > 0)
                context.Report(DiagnosticCodes.ResultSetCursor, Severity.Warning,
                    $"{m.Name} returns {(shape.Cursors.Count == 1 ? "a result set" : $"{shape.Cursors.Count} result sets")} through INOUT refcursor "
                    + $"parameter{(shape.Cursors.Count == 1 ? "" : "s")} {string.Join(", ", shape.Cursors)}: a caller CALLs it and FETCHes the rows "
                    + "from the cursors in the same transaction.");

            var signature = new BodySignature(BodyKind.Procedure, name.Name, bodyParameters) { Shape = shape };
            var body = procedural.Body(p.StatementList?.Statements ?? [], signature, context);
            var routine = new PgCreateRoutine(name, !shape.IsFunction, parameters, body);
            if (shape.IsFunction)
                routine = routine with
                {
                    ReturnsTable = shape.Columns.Select(col => new PgField(_names.Alias(col.Name), MapType(col.Type!, context))).ToList(),
                    Volatility = Volatility(m.Name, model, context.CompatFunctions),
                };
            return new TranslatedObject(m.Name, ObjectKind.Procedure, name.Qualified, Phase.Code, [routine], context.Diagnostics, dependsOn)
            {
                Extensions = context.Extensions.ToList(),
                CompatFunctions = context.CompatFunctions.ToList(),
            };
        }
        catch (UntranslatableException ex)
        {
            return Untranslated(m, name.Qualified, dependsOn, ex.Code, ex.Message, context.Diagnostics);
        }
    }

    /// <summary>The value of a body that is one <c>RETURN expression</c> (inside any BEGIN … END), else null.</summary>
    private static ScalarExpression? SingleReturn(IList<TSqlStatement> statements)
    {
        while (statements is [BeginEndBlockStatement block])
            statements = block.StatementList.Statements;
        return statements is [ReturnStatement { Expression: { } value }] ? value : null;
    }

    private string MapType(SqlServerType type, ExpressionContext c)
    {
        var mapped = _types.Map(type with { UserTypeName = null }, c.Owner);
        foreach (var d in mapped.Diagnostics.Where(d => d.Severity != Severity.Info))
            c.Diagnostics.Add(d with { Object = c.Owner });
        if (mapped.Extension is not null)
            c.Extensions.Add(mapped.Extension);
        return mapped.PgType;
    }

    /// <summary>
    /// Parameters keep their names (without <c>@</c>). OUTPUT parameters are INOUT, DEFAULT NULL. PostgreSQL
    /// needs every parameter after a defaulted one to have a default too; where SQL Server's order
    /// breaks that, the later ones get DEFAULT NULL.
    /// </summary>
    private (List<PgParameter> Pg, List<BodyParameter> Body) Parameters(IList<ProcedureParameter> parameters, SemanticModel model,
        ProceduralTranslator procedural, ExpressionContext c, IReadOnlySet<string>? reserved = null)
    {
        var pg = new List<PgParameter>();
        var body = new List<BodyParameter>();
        bool defaulted = false;
        foreach (var p in parameters)
        {
            if (p.Modifier == ParameterModifier.ReadOnly)
                throw new UntranslatableException(DiagnosticCodes.UnsupportedExpression, $"{p.VariableName.Value} is a table-valued parameter (READONLY).");
            string source = p.VariableName.Value.TrimStart('@');
            string target = _names.Alias(source);
            if (reserved?.Contains(target) == true)
            {
                c.Report(DiagnosticCodes.ParameterRenamed, Severity.Info,
                    $"{p.VariableName.Value} becomes p_{target}: the function also returns a column named {target}, and PostgreSQL does not allow both.");
                target = "p_" + target;
            }
            var type = model.TypeOf(p.DataType, c.Schema);
            PgExpr? value = p.Value is null ? null : procedural.Constant(p.Value, c);
            if (value is null && p.Modifier == ParameterModifier.Output)
                value = PgLiteral.Null; // a caller need not pass an INOUT parameter whose value it does not want
            else if (value is null && defaulted)
            {
                value = PgLiteral.Null;
                c.Report(DiagnosticCodes.ParameterDefaultAdded, Severity.Info,
                    $"{p.VariableName.Value} gets DEFAULT NULL: in PostgreSQL every parameter after one with a default needs a default too.");
            }
            defaulted |= value is not null;
            pg.Add(new PgParameter(target, MapType(type, c), p.Modifier == ParameterModifier.Output ? "INOUT" : "IN", value));
            body.Add(new BodyParameter(source, target, type, p.DataType));
        }
        return (pg, body);
    }

    private readonly Dictionary<ObjectName, string> _volatility = new(ObjectNameComparer.Instance);

    /// <summary>The PostgreSQL compat helpers declared IMMUTABLE (the others are STABLE).</summary>
    private static readonly HashSet<string> ImmutableCompat = new(StringComparer.Ordinal) { "charindex", "datediff", "datepart_week", "isjson", "isnumeric" };

    /// <summary>
    /// VOLATILE when the body writes (table variables, temp tables) or calls a function that
    /// changes; STABLE when it reads tables, calls a function that is not immutable, or works with
    /// dates and times (their text forms depend on settings); IMMUTABLE otherwise.
    /// </summary>
    private string Volatility(ObjectName function, SemanticModel model, IEnumerable<string> compat)
    {
        if (_volatility.TryGetValue(function, out var known))
            return known;
        _volatility[function] = "VOLATILE"; // while it is worked out: recursion is not assumed safe
        var finder = new VolatilityFinder(model, function.Schema, name => model.Routine(name) is null ? "VOLATILE" : Volatility(name, model, []));
        model.ModuleTree(function)?.Accept(finder);
        string result = finder.Result;
        if (result == "IMMUTABLE" && compat.Any(f => !ImmutableCompat.Contains(f)))
            result = "STABLE";
        _volatility[function] = result;
        return result;
    }

    private sealed class VolatilityFinder(SemanticModel model, string schema, Func<ObjectName, string> routine) : TSqlFragmentVisitor
    {
        private int _level; // 0 immutable, 1 stable, 2 volatile

        public string Result => _level switch { 0 => "IMMUTABLE", 1 => "STABLE", _ => "VOLATILE" };

        private void Raise(int level) => _level = Math.Max(_level, level);

        private void Raise(string volatility) => Raise(volatility switch { "IMMUTABLE" => 0, "STABLE" => 1, _ => 2 });

        private static readonly HashSet<string> Immutable = new(StringComparer.OrdinalIgnoreCase)
        {
            "ABS", "CEILING", "FLOOR", "ROUND", "POWER", "SQRT", "SQUARE", "EXP", "LOG", "LOG10", "SIGN", "PI",
            "LEN", "DATALENGTH", "LEFT", "RIGHT", "SUBSTRING", "UPPER", "LOWER", "LTRIM", "RTRIM", "TRIM", "REPLACE",
            "REPLICATE", "REVERSE", "CHARINDEX", "STUFF", "SPACE", "CHAR", "NCHAR", "ASCII", "UNICODE", "ISNULL",
            "COALESCE", "NULLIF", "IIF", "ISNUMERIC",
        };

        private static readonly HashSet<string> Volatile = new(StringComparer.OrdinalIgnoreCase)
        {
            "NEWID", "NEWSEQUENTIALID", "RAND", "CRYPT_GEN_RANDOM",
        };

        public override void Visit(NamedTableReference node)
        {
            if (!node.SchemaObject.BaseIdentifier.Value.StartsWith('#'))
                Raise(1);
        }

        public override void Visit(SchemaObjectFunctionTableReference node) =>
            Raise(model.ResolveRoutine(node.SchemaObject, schema) is { } name ? routine(name) : "STABLE");

        public override void Visit(FunctionCall node)
        {
            string name = node.FunctionName.Value;
            if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers.Count: 1 } target)
            {
                var resolved = model.ResolveRoutine(new SchemaObjectName { Identifiers = { target.MultiPartIdentifier.Identifiers[0], node.FunctionName } }, schema);
                Raise(resolved is null ? "VOLATILE" : routine(resolved));
            }
            else if (node.CallTarget is not null)
                Raise(1);
            else if (Volatile.Contains(name))
                Raise(2);
            else if (!Immutable.Contains(name) || node.OverClause is not null)
                Raise(1);
        }

        public override void Visit(ParameterlessCall node) => Raise(1);

        public override void Visit(GlobalVariableExpression node) => Raise(1);

        public override void Visit(ConvertCall node)
        {
            if (node.Style is not null)
                Raise(1);
        }

        public override void Visit(SqlDataTypeReference node)
        {
            if (node.SqlDataTypeOption is SqlDataTypeOption.Date or SqlDataTypeOption.DateTime or SqlDataTypeOption.DateTime2
                or SqlDataTypeOption.SmallDateTime or SqlDataTypeOption.DateTimeOffset or SqlDataTypeOption.Time)
                Raise(1);
        }

        public override void Visit(InsertStatement node) => Raise(2);

        public override void Visit(UpdateStatement node) => Raise(2);

        public override void Visit(DeleteStatement node) => Raise(2);

        public override void Visit(MergeStatement node) => Raise(2);

        public override void Visit(ExecuteStatement node) => Raise(2);

        public override void Visit(DeclareTableVariableStatement node) => Raise(2);

        public override void Visit(TableValuedFunctionReturnType node) => Raise(2);
    }
}
