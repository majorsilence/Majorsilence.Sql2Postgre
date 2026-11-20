using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// Code that asks SQL Server about itself: sys.* and INFORMATION_SCHEMA.* views, and the metadata
/// functions (OBJECT_ID, SCHEMA_NAME, COL_LENGTH, …). They are answered by the mssql_compat
/// <c>catalog</c> helper, whose views and functions read the SQL Server names of every converted
/// object, so comparisons with SQL Server names, literal or in variables, keep working. Ids are
/// PostgreSQL oids. SERVERPROPERTY, DB_ID, DB_NAME and IS_ROLEMEMBER need no helper.
/// </summary>
public sealed partial class ExpressionTranslator
{
    /// <summary>A catalog view in FROM, as its mssql_compat stand-in under the written name (or alias).</summary>
    public PgFromItem CatalogView(NamedTableReference n, string? alias, ExpressionContext c)
    {
        string schema = n.SchemaObject.SchemaIdentifier!.Value, name = n.SchemaObject.BaseIdentifier.Value;
        if (CatalogViews.Find(schema, name) is not { } view)
            throw new UntranslatableException(DiagnosticCodes.CatalogViewUnsupported, $"{CatalogViews.WhyNot(schema, name)}: {Text(n)}");
        UseCatalog(c, n);
        return new PgTableRef(new PgName(options.CompatSchemaName, view.View), alias ?? names.Alias(name));
    }

    private PgExpr CatalogCall(string function, IReadOnlyList<PgExpr> args, ExpressionContext c, TSqlFragment source)
    {
        UseCatalog(c, source);
        return new PgFunctionCall($"{NameMapper.Quote(options.CompatSchemaName)}.{function}", args);
    }

    private void UseCatalog(ExpressionContext c, TSqlFragment source)
    {
        if (!options.UseCompatSchema)
            throw Untranslatable(source, "a catalog query (it needs the mssql_compat catalog, and the compat schema is disabled)", DiagnosticCodes.CompatDisabled);
        c.CompatFunctions.Add(CatalogViews.Helper);
        const string message = "Catalog views and metadata functions are answered from mssql_compat's catalog of the converted objects, under their SQL Server names. "
            + "Ids are PostgreSQL oids, and objects created after the conversion are not listed.";
        if (!c.Diagnostics.Any(d => d.Code == DiagnosticCodes.CatalogEmulated))
            c.Report(DiagnosticCodes.CatalogEmulated, Severity.Info, message);
    }

    /// <summary>The metadata functions; null for a name that is not one.</summary>
    private PgExpr? CatalogFunction(string name, FunctionCall f, ExpressionContext c)
    {
        var a = f.Parameters;
        PgExpr Arg(int i) => Scalar(a[i], c);
        PgExpr TextArg(int i) => Text(a[i], c);
        void Arity(params int[] allowed)
        {
            if (!allowed.Contains(a.Count))
                throw Untranslatable(f, $"{name} with {a.Count} argument(s)");
        }
        switch (name)
        {
            case "SCHEMA_NAME":
                Arity(0, 1);
                // With no argument, the caller's default schema: dbo.
                return a.Count == 0 ? PgLiteral.String(SemanticModel.DefaultSchema) : CatalogCall("schema_name", [Arg(0)], c, f);
            case "SCHEMA_ID":
                Arity(0, 1);
                return CatalogCall("schema_id", [a.Count == 0 ? PgLiteral.String(SemanticModel.DefaultSchema) : TextArg(0)], c, f);
            case "OBJECT_ID" when a.Count == 0 || TempTableName(a[0]) is null:
                // OBJECT_ID('tempdb..#t') is the temp-table check (WP 3.5).
                Arity(1, 2);
                return CatalogCall("object_id", a.Count == 1 ? [TextArg(0)] : [TextArg(0), TextArg(1)], c, f);
            case "OBJECT_NAME":
                Arity(1, 2);
                return CatalogCall("object_name", [Arg(0)], c, f);
            case "COL_LENGTH":
                Arity(2);
                return CatalogCall("col_length", [TextArg(0), TextArg(1)], c, f);
            case "COLUMNPROPERTY":
                Arity(3);
                return CatalogCall("columnproperty", [Arg(0), TextArg(1), TextArg(2)], c, f);
            case "OBJECTPROPERTY" or "OBJECTPROPERTYEX":
                Arity(2);
                return CatalogCall("objectproperty", [Arg(0), TextArg(1)], c, f);
            case "DB_ID":
                Arity(0, 1);
                return new PgTemplate("(SELECT CAST(oid AS integer) FROM pg_database WHERE datname = {0})",
                    [a.Count == 0 ? new PgFunctionCall("current_database", []) : TextArg(0)]);
            case "DB_NAME" when a.Count == 1:
                return new PgTemplate("(SELECT CAST(datname AS text) FROM pg_database WHERE oid = CAST({0} AS oid))", [Arg(0)]);
            case "IS_ROLEMEMBER":
                Arity(1, 2);
                // db_owner is the database's owner; another role must exist (NULL otherwise, as in SQL Server).
                var member = a.Count == 2 ? TextArg(1) : new PgKeyword("current_user");
                return new PgTemplate("CASE WHEN {0} = 'db_owner' THEN CAST(pg_has_role({1}, (SELECT datdba FROM pg_database WHERE datname = current_database()), 'MEMBER') AS integer) "
                    + "ELSE (SELECT CAST(pg_has_role({1}, r.oid, 'MEMBER') AS integer) FROM pg_roles AS r WHERE r.rolname = {0}) END", [TextArg(0), member]);
            case "SERVERPROPERTY" when a is [StringLiteral { Value: var property }]:
                return ServerProperty(property, f, c);
            default:
                return null;
        }
    }

    /// <summary>
    /// SERVERPROPERTY: the server's language (WP 4.7), its version and collation from PostgreSQL,
    /// and the features PostgreSQL does or does not have, as SQL Server reports a missing feature
    /// (In-Memory OLTP, PolyBase, availability groups: 0; full-text search: 1).
    /// </summary>
    private PgExpr ServerProperty(string property, FunctionCall f, ExpressionContext c)
    {
        PgExpr? value = ConstantServerProperty(property) is { } constant
            ? new PgLiteral(constant.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : property.ToUpperInvariant() switch
            {
                "LCID" => Compat("fulltext_lcid", [], c, f),
                "PRODUCTVERSION" => new PgFunctionCall("current_setting", [PgLiteral.String("server_version")]),
                "SERVERNAME" or "MACHINENAME" or "INSTANCENAME" => new PgFunctionCall("current_setting", [PgLiteral.String("cluster_name")]),
                "COLLATION" => new PgTemplate("(SELECT CAST(datcollate AS text) FROM pg_database WHERE datname = current_database())", []),
                _ => null,
            };
        if (value is null)
            throw Untranslatable(f, $"SERVERPROPERTY('{property}')", DiagnosticCodes.CatalogViewUnsupported);
        c.Report(DiagnosticCodes.CatalogEmulated, Severity.Info,
            $"SERVERPROPERTY('{property}') is answered for PostgreSQL: version, name and collation from the server; a feature PostgreSQL lacks (In-Memory OLTP, PolyBase, availability groups) is 0, full-text search is 1, EngineEdition is 3.");
        return value;
    }

    /// <summary>
    /// The SERVERPROPERTY values that are the same on every PostgreSQL server: the features it has
    /// (full-text search: 1) or lacks (In-Memory OLTP, PolyBase, availability groups: 0), and
    /// EngineEdition 3 (5 is Azure SQL Database, which code tests for; this is not that).
    /// </summary>
    public static int? ConstantServerProperty(string property) => property.ToUpperInvariant() switch
    {
        "ISFULLTEXTINSTALLED" => 1,
        "ISXTPSUPPORTED" or "ISPOLYBASEINSTALLED" or "ISHADRENABLED" or "ISCLUSTERED" or "ISSINGLEUSER" or "ISADVANCEDANALYTICSINSTALLED" => 0,
        "ENGINEEDITION" => 3,
        _ => null,
    };

    /// <summary>
    /// Whether a condition over constant server properties (<c>SERVERPROPERTY(N'IsXTPSupported') = 0</c>,
    /// <c>CAST(SERVERPROPERTY('EngineEdition') AS int) &lt;&gt; 5</c>) is always true or always false
    /// in PostgreSQL; null when it depends on anything else. <paramref name="property"/> is the
    /// first property it reads.
    /// </summary>
    public static bool? ConstantCondition(BooleanExpression condition, out string? property)
    {
        string? first = null;
        bool? Test(BooleanExpression b) => b switch
        {
            BooleanParenthesisExpression p => Test(p.Expression),
            BooleanNotExpression n => !Test(n.Expression),
            BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.And } a =>
                Test(a.FirstExpression) is { } x && Test(a.SecondExpression) is { } y ? x && y : null,
            BooleanBinaryExpression { BinaryExpressionType: BooleanBinaryExpressionType.Or } o =>
                Test(o.FirstExpression) is { } x && Test(o.SecondExpression) is { } y ? x || y : null,
            BooleanComparisonExpression cmp when Value(cmp.FirstExpression) is { } l && Value(cmp.SecondExpression) is { } r =>
                cmp.ComparisonType switch
                {
                    BooleanComparisonType.Equals => l == r,
                    BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation => l != r,
                    BooleanComparisonType.GreaterThan => l > r,
                    BooleanComparisonType.LessThan => l < r,
                    BooleanComparisonType.GreaterThanOrEqualTo => l >= r,
                    BooleanComparisonType.LessThanOrEqualTo => l <= r,
                    _ => null,
                },
            _ => null,
        };
        long? Value(ScalarExpression e)
        {
            switch (e)
            {
                case ParenthesisExpression p:
                    return Value(p.Expression);
                case IntegerLiteral i when long.TryParse(i.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long n):
                    return n;
                case CastCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Int or SqlDataTypeOption.BigInt or SqlDataTypeOption.SmallInt or SqlDataTypeOption.TinyInt } } cast:
                    return Value(cast.Parameter);
                case ConvertCall { DataType: SqlDataTypeReference { SqlDataTypeOption: SqlDataTypeOption.Int or SqlDataTypeOption.BigInt or SqlDataTypeOption.SmallInt or SqlDataTypeOption.TinyInt }, Style: null } convert:
                    return Value(convert.Parameter);
                case FunctionCall { CallTarget: null, Parameters: [StringLiteral { Value: var name }] } f
                    when f.FunctionName.Value.Equals("SERVERPROPERTY", StringComparison.OrdinalIgnoreCase) && ConstantServerProperty(name) is { } v:
                    first ??= name;
                    return v;
                default:
                    return null;
            }
        }
        var result = Test(condition);
        property = result is null ? null : first;
        return property is null ? null : result;
    }
}
