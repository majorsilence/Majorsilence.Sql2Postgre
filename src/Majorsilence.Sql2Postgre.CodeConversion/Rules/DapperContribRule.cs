using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.Names;
using Microsoft.CodeAnalysis;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>
/// Dapper.Contrib, in files that import <c>Dapper.Contrib.Extensions</c>: <c>[Table("dbo.Item")]</c>
/// names become the converted table's (Contrib writes them into its SQL as they are), and the
/// adapter hooks that pick SQL Server's dialect are reported.
/// </summary>
public sealed class DapperContribRule : ICodeRule
{
    public void Apply(CodeContext context)
    {
        if (!context.Imports.Contains("Dapper.Contrib.Extensions"))
            return;
        var names = new NameMapper(context.Options.Schema);
        foreach (var node in context.Root.DescendantNodes())
        {
            (string Name, SyntaxNode? Argument) attribute = node switch
            {
                CSS.AttributeSyntax a => (a.Name.ToString(), a.ArgumentList?.Arguments.FirstOrDefault()?.Expression),
                VBS.AttributeSyntax a => (a.Name.ToString(), (a.ArgumentList?.Arguments.FirstOrDefault() as VBS.SimpleArgumentSyntax)?.Expression),
                _ => ("", null),
            };
            if (attribute.Name is not ("Table" or "TableAttribute") || attribute.Argument is null
                || context.Language.StringValue(attribute.Argument) is not { } written || Table(written) is not { } source)
                continue;
            var table = context.Options.Model?.ResolveRelation(source.Written, null) ?? new ObjectName(source.Written.SchemaIdentifier?.Value ?? "dbo", source.Written.BaseIdentifier.Value);
            string converted = names.Relation(table).Qualified;
            if (converted != written)
                context.Edit(attribute.Argument, context.Language.StringLiteral(converted, attribute.Argument), "Dapper.Contrib table name as converted");
        }
        Entities(context);
        foreach (var token in context.Root.DescendantTokens().Where(t => t.ValueText is "GetDatabaseType" or "ISqlAdapter"))
            context.Report(DiagnosticCodes.DapperContribAdapter, Severity.Info, token,
                $"{token.ValueText} picks Dapper.Contrib's SQL dialect: with Npgsql, Contrib picks its PostgreSQL adapter by the connection's type name (npgsqlconnection).");
    }

    /// <summary>
    /// A Contrib entity (a class with <c>[Table]</c>, or a <c>[Key]</c>/<c>[ExplicitKey]</c> property)
    /// whose property names are not the converted column names: Contrib's PostgreSQL adapter quotes
    /// them in INSERT, UPDATE and DELETE, so they must match exactly.
    /// </summary>
    private static void Entities(CodeContext c)
    {
        var style = c.Options.Schema.NameStyle;
        if (style == NameStyle.Preserve)
            return;
        foreach (var type in c.Root.DescendantNodes().Where(n => n is CSS.TypeDeclarationSyntax or VBS.TypeBlockSyntax))
        {
            var properties = type switch
            {
                CSS.TypeDeclarationSyntax cs => cs.Members.OfType<CSS.PropertyDeclarationSyntax>()
                    .Select(p => (Name: p.Identifier.ValueText, Attributes: p.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList())).ToList(),
                VBS.TypeBlockSyntax vb => vb.Members.OfType<VBS.PropertyStatementSyntax>()
                    .Concat(vb.Members.OfType<VBS.PropertyBlockSyntax>().Select(b => b.PropertyStatement))
                    .Select(p => (Name: p.Identifier.ValueText, Attributes: p.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()).ToList())).ToList(),
                _ => [],
            };
            var typeAttributes = type switch
            {
                CSS.TypeDeclarationSyntax cs => cs.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()),
                VBS.TypeBlockSyntax vb => vb.BlockStatement.AttributeLists.SelectMany(a => a.Attributes).Select(a => a.Name.ToString()),
                _ => [],
            };
            bool entity = typeAttributes.Any(a => a is "Table" or "TableAttribute")
                          || properties.Any(p => p.Attributes.Any(a => a is "Key" or "ExplicitKey" or "KeyAttribute" or "ExplicitKeyAttribute"));
            if (!entity)
                continue;
            var mismatched = properties.Where(p => !p.Attributes.Any(a => a is "Computed" or "Write" or "ComputedAttribute" or "WriteAttribute"))
                .Where(p => NameMapper.ApplyStyle(p.Name, style) != p.Name).Select(p => $"{p.Name} → {NameMapper.ApplyStyle(p.Name, style)}").ToList();
            if (mismatched.Count == 0)
                continue;
            SyntaxNodeOrToken at = type switch
            {
                CSS.TypeDeclarationSyntax cs => cs.Identifier,
                VBS.TypeBlockSyntax vb => vb.BlockStatement.Identifier,
                _ => type,
            };
            c.Report(DiagnosticCodes.DapperContribColumns, Severity.Warning, at,
                $"Dapper.Contrib's PostgreSQL adapter quotes property names in Insert, Update and Delete, so they must equal the converted column names ({string.Join(", ", mismatched)}). Get and GetAll work. Rename the properties, or write those statements with Dapper's Execute.");
        }
    }

    private sealed record WrittenName(SchemaObjectName Written);

    private static WrittenName? Table(string written)
    {
        var parsed = new TSql160Parser(true).Parse(new StringReader($"SELECT 1 FROM {written}"), out var errors);
        return errors.Count == 0 && parsed is TSqlScript { Batches: [{ Statements: [SelectStatement { QueryExpression: QuerySpecification { FromClause.TableReferences: [NamedTableReference t] } }] }] }
            ? new WrittenName(t.SchemaObject)
            : null;
    }
}
