using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Majorsilence.Sql2Postgre.Translation.Sql;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Expressions;

/// <param name="Expression">The translation, or null when the expression could not be translated.</param>
public sealed record ExpressionResult(PgExpr? Expression, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Extensions)
{
    public IReadOnlyList<string> CompatFunctions { get; init; } = [];

    /// <summary>The user-defined functions the expression calls (created with the tables, before them).</summary>
    public IReadOnlyList<ObjectName> UserFunctions { get; init; } = [];
}

/// <summary>
/// Translates the T-SQL expressions found in schema objects — column defaults, check
/// constraints, computed columns and filtered-index predicates — through the full
/// <see cref="ExpressionTranslator"/>, with the table's own columns as the only names in scope.
/// </summary>
/// <remarks>
/// SQL Server stores these expressions in its normalized form (<c>((0))</c>, <c>([qty]&gt;(0))</c>);
/// either that or a script's form is accepted. User-defined functions are not translated here:
/// a table that uses one in a default or computed column needs that function created first, which
/// comes with the procedural translation of Stage 3.
/// </remarks>
public sealed class SchemaExpressionTranslator
{
    private readonly ExpressionTranslator _translator;
    private readonly SortedSet<string> _usedCompat = new(StringComparer.Ordinal);

    /// <summary>Every mssql_compat helper a translated expression has called so far.</summary>
    public IReadOnlyCollection<string> UsedCompatFunctions => _usedCompat;

    private readonly List<ObjectName> _called = [];

    /// <param name="tableFunction">Whether a table definition may call the user-defined function (it can be created before the tables).</param>
    public SchemaExpressionTranslator(ConversionOptions options, NameMapper names, TypeMapper types, SemanticModel? model = null,
        Func<ObjectName, bool>? tableFunction = null)
    {
        _translator = new ExpressionTranslator(options, names, types, model ?? SemanticModel.Build(Empty))
        {
            UserFunctions = false,
            TableFunction = tableFunction is null ? null : name =>
            {
                if (!tableFunction(name))
                    return false;
                _called.Add(name);
                return true;
            },
        };
    }

    private static readonly SourceDatabase Empty = new("", "", 160, [], [], [], [], [], []);

    /// <param name="table">The table the expression belongs to.</param>
    /// <param name="columns">The table's columns, the only names in scope.</param>
    /// <param name="targetType">For a default: the column's type, so <c>1</c> on a <c>bit</c> column becomes <c>true</c>.</param>
    /// <param name="immutable">The expression is a generated column's, which PostgreSQL requires to be immutable.</param>
    public ExpressionResult Translate(string tsql, ObjectName table, IReadOnlyList<Column> columns,
        SqlServerType? targetType = null, bool isPredicate = false, bool immutable = false)
    {
        var context = Context(table, columns, immutable);
        _called.Clear();
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        IList<ParseError> errors;
        TSqlFragment? fragment = isPredicate
            ? parser.ParseBooleanExpression(new StringReader(tsql), out errors)
            : parser.ParseExpression(new StringReader(tsql), out errors);
        if (errors.Count > 0 || fragment is null)
            return Fail(context, table, DiagnosticCodes.UnsupportedExpression, $"'{tsql}' does not parse as a T-SQL expression: {errors.FirstOrDefault()?.Message}");
        try
        {
            PgExpr result = fragment is BooleanExpression b ? _translator.Boolean(b, context) : _translator.Scalar((ScalarExpression)fragment, context);
            if (targetType?.Name == "bit")
                result = ExpressionTranslator.AsBoolean(result);
            _usedCompat.UnionWith(context.CompatFunctions);
            return new ExpressionResult(result, context.Diagnostics, context.Extensions.ToList())
            {
                CompatFunctions = context.CompatFunctions.ToList(),
                UserFunctions = _called.Distinct().ToList(),
            };
        }
        catch (UntranslatableException ex)
        {
            return Fail(context, table, ex.Code, $"'{tsql}': {ex.Message}");
        }
    }

    /// <summary>
    /// The PostgreSQL type of an expression's value, when the columns and literals it uses make it
    /// evident. Exact decimals of open precision become an unconstrained <c>numeric</c>, since SQL
    /// Server's result-precision rules do not carry over. Null when unknown.
    /// </summary>
    public string? InferType(string tsql, ObjectName table, IReadOnlyList<Column> columns)
    {
        var fragment = new TSql160Parser(initialQuotedIdentifiers: true).ParseExpression(new StringReader(tsql), out var errors);
        if (errors.Count > 0 || fragment is null)
            return null;
        var type = _translator.TypeOf(fragment, Context(table, columns, false));
        return type?.Name switch
        {
            null => null,
            "decimal" or "numeric" when type.Precision is null => "numeric",
            "money" or "smallmoney" => "numeric",
            "nvarchar" or "varchar" when type.Length is null || type.IsMax => "text",
            _ => _translator.Types.Map(type).PgType,
        };
    }

    private static ExpressionContext Context(ObjectName table, IReadOnlyList<Column> columns, bool immutable)
    {
        var scope = new Scope();
        scope.Bind(new Binding(table.Name, new RelationInfo(table, ObjectKind.Table, columns.Select(c => new ColumnInfo(c.Name, c.Type)).ToList())));
        return new ExpressionContext(scope, table.Schema, table) { QualifyColumns = false, RequireImmutable = immutable };
    }

    private static ExpressionResult Fail(ExpressionContext context, ObjectName table, string code, string message) =>
        new(null, [.. context.Diagnostics, new Diagnostic(code, Severity.Error, table, message)], []);
}
