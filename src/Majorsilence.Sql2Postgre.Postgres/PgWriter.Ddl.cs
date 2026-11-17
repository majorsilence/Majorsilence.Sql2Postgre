using System.Globalization;
using System.Text;
using Majorsilence.Sql2Postgre.Translation.Names;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Postgres;

public static partial class PgWriter
{
    private const string Indent = "    ";

    /// <summary>
    /// Writes a statement for the given PostgreSQL major version: keywords upper case, four-space
    /// indent, one column per line, ending in <c>;\n</c>. Version rules: a non-stored generated
    /// column is <c>VIRTUAL</c> on 18 and later and <c>STORED</c> before (the translator reports
    /// that change, since it knows the source).
    /// </summary>
    public static string Write(PgStatement statement, int targetVersion) => statement switch
    {
        PgCreateSchema s => $"CREATE SCHEMA IF NOT EXISTS {Q(s.Name)};\n",
        PgCreateExtension e => $"CREATE EXTENSION IF NOT EXISTS {Q(e.Name)};\n",
        PgCreateCollation c => $"CREATE COLLATION IF NOT EXISTS {c.Name.Qualified} (provider = {c.Provider}, locale = {Str(c.Locale)}, deterministic = {(c.Deterministic ? "true" : "false")});\n",
        PgCreateDomain d => $"CREATE DOMAIN {d.Name.Qualified} AS {d.BaseType}{(d.NotNull ? " NOT NULL" : "")};\n",
        PgCreateCompositeType t => $"CREATE TYPE {t.Name.Qualified} AS (\n{string.Join(",\n", t.Fields.Select(f => $"{Indent}{Q(f.Name)} {f.Type}"))}\n);\n",
        PgCreateSequence s => Sequence(s),
        PgCreateTable t => Table(t, targetVersion),
        PgAddPrimaryKey pk => $"ALTER TABLE {pk.Table.Qualified} ADD CONSTRAINT {Q(pk.Name)} PRIMARY KEY ({Columns(pk.Columns)});\n",
        PgAddUnique u => $"ALTER TABLE {u.Table.Qualified} ADD CONSTRAINT {Q(u.Name)} UNIQUE ({Columns(u.Columns)});\n",
        PgAddCheck c => $"ALTER TABLE {c.Table.Qualified} ADD CONSTRAINT {Q(c.Name)} CHECK ({Expression(c.Condition)}){(c.NotValid ? " NOT VALID" : "")};\n",
        PgAddForeignKey fk => ForeignKey(fk),
        PgCreateIndex i => Index(i),
        PgUntranslated u => Untranslated(u),
        PgCreateRoutine r => Routine(r, targetVersion),
        PgDropTable d => $"DROP TABLE {(d.IfExists ? "IF EXISTS " : "")}{d.Name.Qualified};\n",
        PgCreateTrigger t => Trigger(t),
        PgTruncate t => $"TRUNCATE TABLE {t.Table.Qualified}{(t.RestartIdentity ? " RESTART IDENTITY" : "")};\n",
        PgDoBlock d => DoBlock(d, targetVersion),
        PgLineComment c =>string.Concat(c.Text.Replace("\r\n", "\n").Split('\n').Select(l => $"-- {l}".TrimEnd() + "\n")),
        _ => DmlOrQuery(statement),
    };

    private static string Q(string identifier) => NameMapper.Quote(identifier);

    private static string Str(string value) => PgLiteral.String(value).Sql;

    private static string Columns(IEnumerable<string> columns) => string.Join(", ", columns.Select(Q));

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Sequence(PgCreateSequence s)
    {
        var sb = new StringBuilder($"CREATE SEQUENCE {s.Name.Qualified} AS {s.Type} START WITH {N(s.Start)} INCREMENT BY {N(s.Increment)}");
        sb.Append(s.Min is long min ? $" MINVALUE {N(min)}" : " NO MINVALUE");
        sb.Append(s.Max is long max ? $" MAXVALUE {N(max)}" : " NO MAXVALUE");
        sb.Append(s.Cycle ? " CYCLE" : " NO CYCLE");
        return sb.Append(";\n").ToString();
    }

    private static string Table(PgCreateTable t, int targetVersion)
    {
        var lines = t.Columns.Select(c => Indent + Column(c, targetVersion)).ToList();
        var sb = new StringBuilder($"CREATE {(t.Temporary ? "TEMP " : "")}TABLE {t.Name.Qualified} (\n");
        sb.Append(string.Join(",\n", lines));
        sb.Append('\n');
        foreach (var comment in t.Comments)
            sb.Append(string.Concat(Untranslated(comment).Split('\n').Where(l => l.Length > 0).Select(l => Indent + l + "\n")));
        sb.Append(')');
        if (t.OnCommit is not null)
            sb.Append(" ON COMMIT ").Append(t.OnCommit);
        return sb.Append(";\n").ToString();
    }

    private static string Column(PgColumnDef c, int targetVersion)
    {
        var sb = new StringBuilder($"{Q(c.Name)} {c.Type}");
        if (c.Collation is not null)
            sb.Append($" COLLATE {c.Collation}");
        if (c.Generated is { } g)
            sb.Append($" GENERATED ALWAYS AS ({Expression(g.Expression)}) {(g.Stored || targetVersion < 18 ? "STORED" : "VIRTUAL")}");
        if (c.Identity is { } id)
            sb.Append($" GENERATED {(id.Always ? "ALWAYS" : "BY DEFAULT")} AS IDENTITY (START WITH {N(id.Start)} INCREMENT BY {N(id.Increment)})");
        if (c.Default is not null)
            sb.Append($" DEFAULT {Expression(c.Default)}");
        if (c.NotNull)
            sb.Append(" NOT NULL");
        return sb.ToString();
    }

    private static string ForeignKey(PgAddForeignKey fk)
    {
        var sb = new StringBuilder($"ALTER TABLE {fk.Table.Qualified} ADD CONSTRAINT {Q(fk.Name)} FOREIGN KEY ({Columns(fk.Columns)})"
                                   + $" REFERENCES {fk.Referenced.Qualified} ({Columns(fk.ReferencedColumns)})");
        if (fk.OnDelete is not null)
            sb.Append($" ON DELETE {fk.OnDelete}");
        if (fk.OnUpdate is not null)
            sb.Append($" ON UPDATE {fk.OnUpdate}");
        if (fk.NotValid)
            sb.Append(" NOT VALID");
        return sb.Append(";\n").ToString();
    }

    private static string Trigger(PgCreateTrigger t)
    {
        var sb = new StringBuilder($"CREATE TRIGGER {Q(t.Name)} {t.Timing} {t.Event} ON {t.Table.Qualified}\n");
        if (t.NewTable is not null || t.OldTable is not null)
        {
            sb.Append("REFERENCING");
            if (t.NewTable is not null)
                sb.Append(" NEW TABLE AS ").Append(Q(t.NewTable));
            if (t.OldTable is not null)
                sb.Append(" OLD TABLE AS ").Append(Q(t.OldTable));
            sb.Append('\n');
        }
        return sb.Append($"FOR EACH {(t.ForEachRow ? "ROW" : "STATEMENT")}\nEXECUTE FUNCTION {t.Function.Qualified}();\n").ToString();
    }

    private static string Index(PgCreateIndex i)
    {
        var sb = new StringBuilder($"CREATE {(i.Unique ? "UNIQUE " : "")}INDEX {(i.Name.Length == 0 ? "" : Q(i.Name) + " ")}ON {i.Table.Qualified}");
        if (i.Method is not null)
            sb.Append($" USING {i.Method}");
        sb.Append($" ({string.Join(", ", i.Keys.Select(k => Q(k.Column) + (k.Descending ? " DESC" : "")))})");
        if (i.Include.Count > 0)
            sb.Append($" INCLUDE ({Columns(i.Include)})");
        if (i.Where is not null)
            sb.Append($" WHERE {Expression(i.Where)}");
        return sb.Append(";\n").ToString();
    }

    /// <summary>
    /// The original T-SQL in a block comment. PostgreSQL nests block comments, so a <c>/*</c> inside
    /// it is written <c>/ *</c> and a <c>*/</c> is written <c>* /</c>: the comment ends only where it should.
    /// </summary>
    private static string Untranslated(PgUntranslated u) =>
        $"/* sql2pg:untranslated {u.Code}\n{u.OriginalTsql.Replace("\r\n", "\n").Replace("/*", "/ *").Replace("*/", "* /").TrimEnd('\n')}\n*/\n";
}
