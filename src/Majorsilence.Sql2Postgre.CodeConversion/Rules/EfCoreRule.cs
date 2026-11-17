using System.Text;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Postgres;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.Types;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using CSS = Microsoft.CodeAnalysis.CSharp.Syntax;
using VBS = Microsoft.CodeAnalysis.VisualBasic.Syntax;
using DatabaseModel = Majorsilence.Sql2Postgre.Translation.Semantics.SemanticModel;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>
/// Entity Framework Core, in files that import <c>Microsoft.EntityFrameworkCore</c>:
/// <list type="bullet">
/// <item><c>UseSqlServer</c> → <c>UseNpgsql</c>;</item>
/// <item>column types, default and computed SQL through the type mapper and the expression translator;</item>
/// <item>identity columns, and SQL Server-only options removed (S2P8040);</item>
/// <item>EF.Functions methods with no PostgreSQL counterpart (S2P8042) and migrations (S2P8043) reported;</item>
/// <item>with <c>NameStyle = Lower</c>, a loop at the end of <c>OnModelCreating</c> that names every table, column, key, index and foreign key in lower case, as the converted schema does (S2P8041).</item>
/// </list>
/// </summary>
public sealed class EfCoreRule : ICodeRule
{
    /// <summary>Fluent calls with no Npgsql counterpart, removed from their chain.</summary>
    private static readonly HashSet<string> SqlServerOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "IsClustered", "IsMemoryOptimized", "HasFillFactor", "IsSparse", "HasDatabaseMaxSize", "HasServiceTier", "HasPerformanceLevel",
        "IsCreatedOnline", "SortInTempDb", "HasDataCompression", "UseSqlOutputClause", "UseCompatibilityLevel", "UseAzureSqlDefaults",
        "UseCollation", "HasPrecisionAndScale",
    };

    private static readonly HashSet<string> SqlServerFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "DateDiffYear", "DateDiffMonth", "DateDiffWeek", "DateDiffDay", "DateDiffHour", "DateDiffMinute", "DateDiffSecond",
        "DateDiffMillisecond", "DateDiffMicrosecond", "DateDiffNanosecond", "DateFromParts", "DateTimeFromParts", "DateTime2FromParts",
        "DateTimeOffsetFromParts", "SmallDateTimeFromParts", "TimeFromParts", "IsDate", "IsNumeric", "DataLength", "Contains", "FreeText",
        "AtTimeZone",
    };

    public void Apply(CodeContext context)
    {
        if (!context.Imports.Any(i => i.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)))
            return;
        bool cs = context.Root is CSS.CompilationUnitSyntax;
        var names = new NameMapper(context.Options.Schema);
        foreach (var call in Invocations(context.Root))
            Invocation(context, call, names, cs);
        if (context.Path.Replace('\\', '/').Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) || context.Path.StartsWith("Migrations", StringComparison.OrdinalIgnoreCase)
            || context.Text.ToString().Contains("\"SqlServer:", StringComparison.Ordinal))
            context.Report(DiagnosticCodes.EfMigrations, Severity.Warning, context.Root.GetFirstToken(),
                "An EF Core migration made for SQL Server (SqlServer: annotations, SQL Server column types): regenerate the migrations against PostgreSQL (dotnet ef migrations add) from the converted model rather than convert them.");
        Naming(context, cs);
    }

    /// <summary>A fluent call: its method name token, the member access holding it, and its arguments.</summary>
    private sealed record Call(SyntaxToken Method, SyntaxNode Invocation, SyntaxNode? Access, IReadOnlyList<SyntaxNode> Arguments, string Receiver);

    private static IEnumerable<Call> Invocations(SyntaxNode root) => root.DescendantNodes().Select(n => n switch
    {
        CSS.InvocationExpressionSyntax { Expression: CSS.MemberAccessExpressionSyntax ma } i =>
            new Call(ma.Name.Identifier, i, ma, i.ArgumentList.Arguments.Select(a => (SyntaxNode)a.Expression).ToList(), ma.Expression.ToString()),
        VBS.InvocationExpressionSyntax { Expression: VBS.MemberAccessExpressionSyntax ma } i =>
            new Call(ma.Name.Identifier, i, ma, i.ArgumentList?.Arguments.OfType<VBS.SimpleArgumentSyntax>().Select(a => (SyntaxNode)a.Expression).ToList() ?? [], ma.Expression?.ToString() ?? ""),
        _ => null,
    }).OfType<Call>().ToList();

    private static void Invocation(CodeContext c, Call call, NameMapper names, bool cs)
    {
        string method = call.Method.ValueText;
        bool Is(string name) => method.Equals(name, StringComparison.OrdinalIgnoreCase);
        if (Is("UseSqlServer") || Is("UseAzureSql"))
        {
            c.Edit(call.Method, "UseNpgsql", "EF Core provider → Npgsql");
            if (Is("UseAzureSql"))
                c.Report(DiagnosticCodes.EfSqlServerOnly, Severity.Info, call.Method, "UseAzureSql becomes UseNpgsql; Azure SQL options do not carry over.");
        }
        else if (Is("UseIdentityColumn"))
        {
            c.Edit(TextSpan.FromBounds(call.Method.SpanStart, call.Invocation.Span.End), "UseIdentityByDefaultColumn()", "identity column");
            if (call.Arguments.Count > 0)
                c.Report(DiagnosticCodes.EfSqlServerOnly, Severity.Info, call.Method,
                    "UseIdentityColumn's seed and increment are dropped: add .HasIdentityOptions(startValue: …, incrementBy: …) if they matter (the converted schema keeps them).");
        }
        else if (Is("UseIdentityColumns"))
            c.Edit(TextSpan.FromBounds(call.Method.SpanStart, call.Invocation.Span.End), "UseIdentityByDefaultColumns()", "identity columns");
        else if (Is("HasColumnType") && call.Arguments is [var typeArgument, ..] && c.Language.StringValue(typeArgument) is { } type)
        {
            if (ColumnType(type, c.Options) is { } pg && pg != type)
                c.Edit(typeArgument, c.Language.StringLiteral(pg, typeArgument), $"column type {type} → {pg}");
        }
        else if ((Is("HasDefaultValueSql") || Is("HasComputedColumnSql")) && call.Arguments is [var sqlArgument, ..] && c.Language.StringValue(sqlArgument) is { } sql)
        {
            var model = c.Options.Model ?? DatabaseModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], []));
            var translated = CommandTranslator.Translate("SELECT " + sql, model, c.Options.Schema, c.Path);
            if (translated.Diagnostics.Any(d => d.Severity == Severity.Error) || !translated.Sql.StartsWith("SELECT ", StringComparison.Ordinal))
                c.Report(DiagnosticCodes.EfSqlServerOnly, Severity.Warning, sqlArgument, $"{method}(\"{sql}\") could not be translated: write the PostgreSQL expression.");
            else if (translated.Sql["SELECT ".Length..] is var expression && expression != sql)
                c.Edit(sqlArgument, c.Language.StringLiteral(expression, sqlArgument), $"{method} translated");
        }
        else if (Is("HasDefaultSchema") && call.Arguments is [var schemaArgument] && c.Language.StringValue(schemaArgument) is { } schema)
        {
            string converted = names.Schema(schema);
            if (converted != schema)
                c.Edit(schemaArgument, c.Language.StringLiteral(converted, schemaArgument), "default schema as converted");
        }
        else if (SqlServerOnly.Contains(method) && call.Access is not null)
        {
            // Remove ".Method(…)" from the chain, keeping what comes before it.
            var dot = call.Access switch
            {
                CSS.MemberAccessExpressionSyntax m => m.OperatorToken,
                VBS.MemberAccessExpressionSyntax m => m.OperatorToken,
                _ => default,
            };
            c.Edit(TextSpan.FromBounds(dot.SpanStart, call.Invocation.Span.End), "", $"{method} has no PostgreSQL counterpart");
            c.Report(DiagnosticCodes.EfSqlServerOnly, Severity.Info, call.Method, $"{method}(…) is SQL Server's and is removed.");
        }
        else if (Is("IsTemporal") || Is("ToTable") && call.Arguments.Any(a => a.ToString().Contains("IsTemporal", StringComparison.Ordinal)))
            c.Report(DiagnosticCodes.EfSqlServerFunction, Severity.Error, call.Method,
                "Temporal tables (IsTemporal) have no PostgreSQL counterpart: keep history with a trigger into a history table, and query it explicitly.");
        else if (call.Receiver.EndsWith("EF.Functions", StringComparison.Ordinal) && SqlServerFunctions.Contains(method))
            c.Report(DiagnosticCodes.EfSqlServerFunction, Severity.Error, call.Method,
                $"EF.Functions.{method} is SQL Server's: write it with what Npgsql translates (date arithmetic such as (end.Date - start.Date).Days, string methods, or EF.Functions.ILike / to_tsvector for search).");
    }

    /// <summary>A SQL Server column type as the PostgreSQL type the schema converter maps it to.</summary>
    private static string? ColumnType(string type, CodeConversionOptions options)
    {
        var parsed = new TSql160Parser(true).Parse(new StringReader($"DECLARE @x {type};"), out var errors);
        if (errors.Count > 0 || parsed is not TSqlScript { Batches: [{ Statements: [DeclareVariableStatement { Declarations: [{ DataType: { } dataType }] }] }] })
            return null;
        var model = options.Model ?? DatabaseModel.Build(new SourceDatabase("", "", 160, [], [], [], [], [], []));
        var names = new NameMapper(options.Schema);
        return new TypeMapper(options.Schema, names).Map(model.TypeOf(dataType, DatabaseModel.DefaultSchema) with { UserTypeName = null }, null).PgType;
    }

    // ---- naming ----

    /// <summary>
    /// EF Core quotes names exactly as its model has them, and the converted schema's are lower
    /// case: a loop at the end of OnModelCreating names everything the same way.
    /// </summary>
    private static void Naming(CodeContext c, bool cs)
    {
        if (c.Options.Schema.NameStyle == NameStyle.Preserve)
            return;
        foreach (var type in c.Root.DescendantNodes())
        {
            switch (type)
            {
                case CSS.ClassDeclarationSyntax cls when cls.BaseList?.Types.Any(t => t.ToString().Contains("DbContext", StringComparison.Ordinal)) == true:
                    if (c.Options.Schema.NameStyle == NameStyle.SnakeCase)
                    {
                        c.Report(DiagnosticCodes.EfNaming, Severity.Warning, cls.Identifier,
                            "The schema is converted with snake_case names: name the model to match (the EFCore.NamingConventions package's UseSnakeCaseNamingConvention does it).");
                        continue;
                    }
                    var method = cls.Members.OfType<CSS.MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "OnModelCreating");
                    string indent = Indent(c, cls) + "    ";
                    if (method?.Body is { } body)
                    {
                        string builder = method.ParameterList.Parameters.FirstOrDefault()?.Identifier.ValueText ?? "modelBuilder";
                        c.Edit(new TextSpan(LineStart(c, body.CloseBraceToken.SpanStart), 0), CSharpLoop(builder, indent + "    ", c), "name the model as the converted schema");
                    }
                    else if (method is null)
                    {
                        string nl = NewLine(c);
                        c.Edit(new TextSpan(LineStart(c, cls.CloseBraceToken.SpanStart), 0),
                            $"{nl}{indent}protected override void OnModelCreating(ModelBuilder modelBuilder){nl}{indent}{{{nl}{indent}    base.OnModelCreating(modelBuilder);{nl}"
                            + CSharpLoop("modelBuilder", indent + "    ", c) + indent + "}" + nl, "name the model as the converted schema");
                    }
                    else
                    {
                        c.Report(DiagnosticCodes.EfNaming, Severity.Warning, method.Identifier,
                            "OnModelCreating has an expression body: give it a block body and the naming loop sql2pg adds elsewhere (see docs/code-conversion.md).");
                        continue;
                    }
                    c.Report(DiagnosticCodes.EfNaming, Severity.Info, cls.Identifier,
                        "OnModelCreating names every table, column, key, index and foreign key in lower case, as the converted schema does (EF Core quotes names exactly as its model has them).");
                    break;
                case VBS.ClassBlockSyntax vb when vb.Inherits.Any(i => i.Types.Any(t => t.ToString().Contains("DbContext", StringComparison.OrdinalIgnoreCase))):
                    if (c.Options.Schema.NameStyle == NameStyle.SnakeCase)
                    {
                        c.Report(DiagnosticCodes.EfNaming, Severity.Warning, vb.ClassStatement.Identifier,
                            "The schema is converted with snake_case names: name the model to match (the EFCore.NamingConventions package's UseSnakeCaseNamingConvention does it).");
                        continue;
                    }
                    var sub = vb.Members.OfType<VBS.MethodBlockSyntax>().FirstOrDefault(m => m.SubOrFunctionStatement.Identifier.ValueText.Equals("OnModelCreating", StringComparison.OrdinalIgnoreCase));
                    string vbIndent = Indent(c, vb) + "    ";
                    if (sub is not null)
                    {
                        string builder = sub.SubOrFunctionStatement.ParameterList?.Parameters.FirstOrDefault()?.Identifier.Identifier.ValueText ?? "modelBuilder";
                        c.Edit(new TextSpan(LineStart(c, sub.EndSubOrFunctionStatement.SpanStart), 0), VisualBasicLoop(builder, vbIndent + "    ", c), "name the model as the converted schema");
                    }
                    else
                    {
                        string nl = NewLine(c);
                        c.Edit(new TextSpan(LineStart(c, vb.EndClassStatement.SpanStart), 0),
                            $"{nl}{vbIndent}Protected Overrides Sub OnModelCreating(modelBuilder As ModelBuilder){nl}{vbIndent}    MyBase.OnModelCreating(modelBuilder){nl}"
                            + VisualBasicLoop("modelBuilder", vbIndent + "    ", c) + $"{vbIndent}End Sub{nl}", "name the model as the converted schema");
                    }
                    c.Report(DiagnosticCodes.EfNaming, Severity.Info, vb.ClassStatement.Identifier,
                        "OnModelCreating names every table, column, key, index and foreign key in lower case, as the converted schema does (EF Core quotes names exactly as its model has them).");
                    break;
            }
        }
    }

    private static int LineStart(CodeContext c, int position) => c.Text.Lines.GetLineFromPosition(position).Start;

    private static string NewLine(CodeContext c) => c.Text.ToString().Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string Indent(CodeContext c, SyntaxNode node)
    {
        var line = c.Text.Lines.GetLineFromPosition(node.SpanStart);
        return new string(c.Text.ToString(line.Span).TakeWhile(char.IsWhiteSpace).ToArray());
    }

    /// <summary>The schema as the schema map converts it, lower case otherwise.</summary>
    private static string SchemaSwitch(CodeContext c, bool cs, string variable)
    {
        var map = c.Options.Schema.SchemaMap;
        if (map.Count == 0)
            return $"{variable}.ToLowerInvariant()";
        if (cs)
            return $"{variable}.ToLowerInvariant() switch {{ {string.Join(", ", map.Select(m => $"\"{m.Key.ToLowerInvariant()}\" => \"{m.Value}\""))}, var other => other }}";
        return string.Join("", map.Select(m => $"If({variable}.ToLowerInvariant() = \"{m.Key.ToLowerInvariant()}\", \"{m.Value}\", ")) + $"{variable}.ToLowerInvariant()" + new string(')', map.Count);
    }

    private static string CSharpLoop(string builder, string indent, CodeContext c)
    {
        string nl = NewLine(c);
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(indent).Append(text).Append(nl);
        sb.Append(nl);
        Line("// sql2pg: name tables, columns, keys, indexes and foreign keys as the converted schema does.");
        Line($"foreach (var entity in {builder}.Model.GetEntityTypes())");
        Line("{");
        Line("    if (entity.GetTableName() is { } table)");
        Line("        entity.SetTableName(table.ToLowerInvariant());");
        Line("    if (entity.GetSchema() is { } schema)");
        Line($"        entity.SetSchema({SchemaSwitch(c, true, "schema")});");
        Line("    foreach (var property in entity.GetProperties())");
        Line("    {");
        Line("        property.SetColumnName(property.GetColumnName().ToLowerInvariant());");
        Line("        // datetime and datetime2 are converted to timestamp (without time zone); Npgsql's default for DateTime is timestamptz.");
        Line("        if ((property.ClrType == typeof(System.DateTime) || property.ClrType == typeof(System.DateTime?)) && property.FindAnnotation(\"Relational:ColumnType\") is null)");
        Line("            property.SetColumnType(\"timestamp without time zone\");");
        Line("    }");
        Line("    foreach (var key in entity.GetKeys())");
        Line("        key.SetName(key.GetName()?.ToLowerInvariant());");
        Line("    foreach (var foreignKey in entity.GetForeignKeys())");
        Line("        foreignKey.SetConstraintName(foreignKey.GetConstraintName()?.ToLowerInvariant());");
        Line("    foreach (var index in entity.GetIndexes())");
        Line("        index.SetDatabaseName(index.GetDatabaseName()?.ToLowerInvariant());");
        Line("}");
        return sb.ToString();
    }

    private static string VisualBasicLoop(string builder, string indent, CodeContext c)
    {
        string nl = NewLine(c);
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(indent).Append(text).Append(nl);
        Line("' sql2pg: name tables, columns, keys, indexes and foreign keys as the converted schema does.");
        Line($"For Each entity In {builder}.Model.GetEntityTypes()");
        Line("    If entity.GetTableName() IsNot Nothing Then entity.SetTableName(entity.GetTableName().ToLowerInvariant())");
        Line($"    If entity.GetSchema() IsNot Nothing Then entity.SetSchema({SchemaSwitch(c, false, "entity.GetSchema()")})");
        Line("    For Each [property] In entity.GetProperties()");
        Line("        [property].SetColumnName([property].GetColumnName().ToLowerInvariant())");
        Line("        ' datetime and datetime2 are converted to timestamp (without time zone); Npgsql's default for DateTime is timestamptz.");
        Line("        If ([property].ClrType Is GetType(Date) OrElse [property].ClrType Is GetType(Date?)) AndAlso [property].FindAnnotation(\"Relational:ColumnType\") Is Nothing Then");
        Line("            [property].SetColumnType(\"timestamp without time zone\")");
        Line("        End If");
        Line("    Next");
        Line("    For Each key In entity.GetKeys()");
        Line("        key.SetName(key.GetName()?.ToLowerInvariant())");
        Line("    Next");
        Line("    For Each foreignKey In entity.GetForeignKeys()");
        Line("        foreignKey.SetConstraintName(foreignKey.GetConstraintName()?.ToLowerInvariant())");
        Line("    Next");
        Line("    For Each index In entity.GetIndexes()");
        Line("        index.SetDatabaseName(index.GetDatabaseName()?.ToLowerInvariant())");
        Line("    Next");
        Line("Next");
        return sb.ToString();
    }
}
