using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// JSON_MODIFY, ISJSON with a type, and OPENJSON, over json (not jsonb: SQL Server keeps a
/// document's member order and number forms, and so does json).
/// </summary>
public sealed partial class ExpressionTranslator
{
    /// <summary>
    /// JSON_MODIFY(doc, '[append] [lax|strict] $.path', value): the mssql_compat helper, with the
    /// value as JSON. A JSON_MODIFY, JSON_QUERY or FOR JSON result is JSON already; anything else
    /// is converted (a string becomes a JSON string, a bit true/false), as SQL Server does.
    /// </summary>
    private PgExpr JsonModify(FunctionCall f, ExpressionContext c)
    {
        if (f.Parameters[1] is not StringLiteral { Value: var written })
            throw Untranslatable(f, "a JSON path that is not a literal", DiagnosticCodes.JsonPathUnsupported);
        string text = written.Trim();
        bool append = false, strict = false;
        while (true)
        {
            if (text.StartsWith("append ", StringComparison.OrdinalIgnoreCase))
                (append, text) = (true, text[7..].TrimStart());
            else if (text.StartsWith("strict ", StringComparison.OrdinalIgnoreCase))
                (strict, text) = (true, text[7..].TrimStart());
            else if (text.StartsWith("lax ", StringComparison.OrdinalIgnoreCase))
                text = text[4..].TrimStart();
            else
                break;
        }
        string path = JsonPath(new StringLiteral { Value = text }, f);
        var call = Compat("json_modify",
        [
            new PgCast(Text(f.Parameters[0], c), "json"),
            new PgCast(PgLiteral.String(path), "text[]"),
            JsonValueOf(f.Parameters[2], c),
            strict ? PgLiteral.True : new PgLiteral("false"),
            append ? PgLiteral.True : new PgLiteral("false"),
        ], c, f);
        return new PgCast(call, "text");
    }

    /// <summary>A value put into JSON: JSON as it is when it comes from JSON_MODIFY, JSON_QUERY or FOR JSON; otherwise to_json of the value.</summary>
    private PgExpr JsonValueOf(ScalarExpression e, ExpressionContext c)
    {
        if (e is NullLiteral)
            return new PgCast(PgLiteral.Null, "json");
        if (e is ParenthesisExpression { Expression: var inner })
            return JsonValueOf(inner, c);
        if (e is FunctionCall { CallTarget: null, FunctionName.Value: var name } && name.ToUpperInvariant() is "JSON_MODIFY" or "JSON_QUERY")
            return new PgCast(Scalar(e, c), "json");
        if (e is ScalarSubquery { QueryExpression: QuerySpecification { ForClause: JsonForClause } })
            return new PgCast(Scalar(e, c), "json");
        var type = TypeOf(e, c);
        PgExpr value = Scalar(e, c);
        if (type is null || TypeRules.IsString(type) || TypeRules.IsDateTime(type))
            value = new PgCast(value, "text");
        return new PgFunctionCall("to_json", [value]);
    }

    /// <summary>ISJSON(s, VALUE | ARRAY | OBJECT | SCALAR).</summary>
    private PgExpr IsJsonOfType(FunctionCall f, ExpressionContext c)
    {
        string? kind = f.Parameters[1] switch
        {
            ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [var id] } => id.Value,
            StringLiteral s => s.Value,
            _ => null,
        };
        if (kind?.ToUpperInvariant() is not ("VALUE" or "ARRAY" or "OBJECT" or "SCALAR"))
            throw Untranslatable(f, "ISJSON with this type");
        return Compat("isjson_type", [new PgCast(Text(f.Parameters[0], c), "text"), PgLiteral.String(kind.ToUpperInvariant())], c, f);
    }

    /// <summary>
    /// OPENJSON(doc [, path]) in FROM. Without WITH: the mssql_compat helper's key, value and type
    /// rows. With WITH (col type ['path'] [AS JSON], …): one row per element of the array at the
    /// path (an object is one row), each column read from it by its path (<c>$.col</c> when none):
    /// a scalar converted to the column's type, NULL for an object or array; or with AS JSON the
    /// object or array as text, NULL for a scalar.
    /// </summary>
    public PgFromItem OpenJson(OpenJsonTableReference o, ExpressionContext c)
    {
        PgExpr doc = new PgCast(Text(o.Variable, c), "json");
        if (o.RowPattern is { } rowPattern)
            doc = new PgBinary("#>", doc, PgLiteral.String(JsonPath(rowPattern, o)));
        string alias = names.Alias(o.Alias?.Value ?? "openjson");
        if (o.SchemaDeclarationItems.Count == 0)
            return new PgFunctionRef(Compat("openjson", [doc], c, o) is PgFunctionCall call ? call.Name : "openjson", [doc], alias, []);

        var element = new PgColumnRef("value", "e");
        var targets = new List<PgTarget>();
        foreach (var item in o.SchemaDeclarationItems)
        {
            string column = item.ColumnDefinition.ColumnIdentifier.Value;
            var pathLiteral = item.Mapping is StringLiteral given ? given : new StringLiteral { Value = "$." + column };
            PgExpr node = new PgBinary("#>", element, PgLiteral.String(JsonPath(pathLiteral, o)));
            var isStructure = new PgInList(new PgFunctionCall("json_typeof", [node]), [PgLiteral.String("object"), PgLiteral.String("array")]);
            PgExpr value;
            if (item.AsJson)
                value = new PgCase(null, [new PgWhen(isStructure, new PgCast(node, "text"))], null);
            else
            {
                var type = model.TypeOf(item.ColumnDefinition.DataType, c.Schema);
                var mapped = types.Map(type with { UserTypeName = null }, c.Owner);
                value = new PgCase(null, [new PgWhen(isStructure, PgLiteral.Null)],
                    new PgCast(new PgBinary("#>>", node, PgLiteral.String("{}")), mapped.PgType));
            }
            targets.Add(new PgTarget(value, names.Alias(column)));
        }
        var rows = new PgFunctionCall("json_array_elements", [new PgCase(null,
            [new PgWhen(new PgBinary("=", new PgFunctionCall("json_typeof", [doc]), PgLiteral.String("array")), doc)],
            new PgFunctionCall("json_build_array", [doc]))]);
        return new PgSubqueryRef(new PgSelect
        {
            Targets = targets,
            From = [new PgFunctionRef(rows.Name, rows.Arguments, "e", ["value"])],
        }, alias, []);
    }
}
