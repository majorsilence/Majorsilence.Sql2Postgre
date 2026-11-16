using Majorsilence.Sql2Postgre.Translation.Names;

namespace Majorsilence.Sql2Postgre.Translation.PgAst;

/// <summary>A statement: one <c>CREATE</c> or <c>ALTER</c>, written ending in <c>;</c>.</summary>
public abstract record PgStatement : PgNode;

public sealed record PgCreateSchema(string Name) : PgStatement;

public sealed record PgCreateExtension(string Name) : PgStatement;

/// <summary><c>CREATE COLLATION IF NOT EXISTS name (provider = …, locale = '…', deterministic = …)</c>.</summary>
public sealed record PgCreateCollation(PgName Name, string Provider, string Locale, bool Deterministic) : PgStatement;

public sealed record PgCreateDomain(PgName Name, string BaseType, bool NotNull) : PgStatement;

public sealed record PgField(string Name, string Type);

/// <summary><c>CREATE TYPE name AS (field type, …)</c>.</summary>
public sealed record PgCreateCompositeType(PgName Name, IReadOnlyList<PgField> Fields) : PgStatement;

public sealed record PgCreateSequence(PgName Name, string Type, long Start, long Increment, long? Min, long? Max, bool Cycle) : PgStatement;

/// <summary><c>GENERATED {BY DEFAULT | ALWAYS} AS IDENTITY (START WITH … INCREMENT BY …)</c>.</summary>
public sealed record PgIdentity(long Start, long Increment, bool Always = false);

/// <summary><c>GENERATED ALWAYS AS (expr) {STORED | VIRTUAL}</c>; VIRTUAL needs PostgreSQL 18.</summary>
public sealed record PgGenerated(PgExpr Expression, bool Stored);

public sealed record PgColumnDef(string Name, string Type, bool NotNull = false, PgExpr? Default = null,
    PgIdentity? Identity = null, PgGenerated? Generated = null, string? Collation = null);

/// <param name="Comments">Comments written inside the table body, before the closing parenthesis (untranslated defaults…).</param>
public sealed record PgCreateTable(PgName Name, IReadOnlyList<PgColumnDef> Columns) : PgStatement
{
    public IReadOnlyList<PgUntranslated> Comments { get; init; } = [];

    /// <summary>CREATE TEMP TABLE (the name has no schema).</summary>
    public bool Temporary { get; init; }

    /// <summary>A temporary table's ON COMMIT action: <c>DROP</c>, <c>DELETE ROWS</c> or <c>PRESERVE ROWS</c>.</summary>
    public string? OnCommit { get; init; }
}

/// <summary><c>DROP TABLE [IF EXISTS] name</c>.</summary>
public sealed record PgDropTable(PgName Name, bool IfExists = true) : PgStatement;

public sealed record PgAddPrimaryKey(PgName Table, string Name, IReadOnlyList<string> Columns) : PgStatement;

public sealed record PgAddUnique(PgName Table, string Name, IReadOnlyList<string> Columns) : PgStatement;

public sealed record PgAddCheck(PgName Table, string Name, PgExpr Condition, bool NotValid = false) : PgStatement;

/// <param name="OnDelete">The referential action as PostgreSQL spells it, or null for NO ACTION.</param>
public sealed record PgAddForeignKey(PgName Table, string Name, IReadOnlyList<string> Columns, PgName Referenced,
    IReadOnlyList<string> ReferencedColumns, string? OnDelete = null, string? OnUpdate = null, bool NotValid = false) : PgStatement;

public sealed record PgIndexKey(string Column, bool Descending = false);

/// <param name="Method">The access method (<c>gist</c>…), or null for the default B-tree.</param>
public sealed record PgCreateIndex(string Name, PgName Table, IReadOnlyList<PgIndexKey> Keys, IReadOnlyList<string> Include,
    bool Unique = false, PgExpr? Where = null, string? Method = null) : PgStatement;

/// <summary>
/// The original T-SQL of something that could not be translated, kept in the output as a block
/// comment headed by its diagnostic code, at the place the translation would have gone.
/// </summary>
public sealed record PgUntranslated(string Code, string OriginalTsql) : PgStatement;

/// <summary>A line of explanation in the output: <c>-- text</c>.</summary>
public sealed record PgLineComment(string Text) : PgStatement;
