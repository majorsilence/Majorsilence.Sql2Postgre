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

/// <summary>
/// <c>CREATE TRIGGER name {AFTER | INSTEAD OF} event ON table [REFERENCING NEW TABLE AS …
/// OLD TABLE AS …] FOR EACH {STATEMENT | ROW} EXECUTE FUNCTION function()</c>.
/// </summary>
/// <param name="Timing"><c>AFTER</c> or <c>INSTEAD OF</c>.</param>
/// <param name="Event"><c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c>.</param>
public sealed record PgCreateTrigger(string Name, PgName Table, string Timing, string Event, PgName Function) : PgStatement
{
    /// <summary>The transition table of new rows (<c>inserted</c>).</summary>
    public string? NewTable { get; init; }

    /// <summary>The transition table of old rows (<c>deleted</c>).</summary>
    public string? OldTable { get; init; }

    public bool ForEachRow { get; init; }
}

/// <summary><c>TRUNCATE TABLE name [RESTART IDENTITY]</c>.</summary>
public sealed record PgTruncate(PgName Table, bool RestartIdentity) : PgStatement;

public sealed record PgAddPrimaryKey(PgName Table, string Name, IReadOnlyList<string> Columns) : PgStatement;

public sealed record PgAddUnique(PgName Table, string Name, IReadOnlyList<string> Columns) : PgStatement;

public sealed record PgAddCheck(PgName Table, string Name, PgExpr Condition, bool NotValid = false) : PgStatement;

/// <param name="OnDelete">The referential action as PostgreSQL spells it, or null for NO ACTION.</param>
public sealed record PgAddForeignKey(PgName Table, string Name, IReadOnlyList<string> Columns, PgName Referenced,
    IReadOnlyList<string> ReferencedColumns, string? OnDelete = null, string? OnUpdate = null, bool NotValid = false) : PgStatement;

/// <param name="Expression">An expression key (<c>to_tsvector('english', body)</c>) in place of <see cref="Column"/>.</param>
/// <param name="OperatorClass">The key's operator class (<c>gin_trgm_ops</c>), or null for the default.</param>
public sealed record PgIndexKey(string Column, bool Descending = false, PgExpr? Expression = null, string? OperatorClass = null);

/// <param name="Name">The index name; empty lets PostgreSQL choose one (a temp table's index).</param>
/// <param name="Method">The access method (<c>gist</c>…), or null for the default B-tree.</param>
public sealed record PgCreateIndex(string Name, PgName Table, IReadOnlyList<PgIndexKey> Keys, IReadOnlyList<string> Include,
    bool Unique = false, PgExpr? Where = null, string? Method = null) : PgStatement;

/// <summary><c>DROP INDEX [IF EXISTS] schema.name</c>.</summary>
public sealed record PgDropIndex(PgName Name, bool IfExists) : PgStatement;

/// <summary><c>REINDEX INDEX name</c>, or <c>REINDEX TABLE name</c> when <paramref name="Table"/> is set.</summary>
public sealed record PgReindex(PgName Name, bool Table = false) : PgStatement;

/// <summary><c>ALTER TABLE t DROP CONSTRAINT [IF EXISTS] name</c>.</summary>
public sealed record PgDropConstraint(PgName Table, string Name, bool IfExists = true) : PgStatement;

/// <summary><c>ALTER TABLE t VALIDATE CONSTRAINT name</c>.</summary>
public sealed record PgValidateConstraint(PgName Table, string Name) : PgStatement;

/// <summary><c>ALTER TABLE t ADD COLUMN …</c>.</summary>
public sealed record PgAddColumn(PgName Table, PgColumnDef Column) : PgStatement;

/// <summary><c>ALTER TABLE t DROP COLUMN [IF EXISTS] c</c>.</summary>
public sealed record PgDropColumn(PgName Table, string Column, bool IfExists = false) : PgStatement;

/// <summary>
/// <c>ALTER TABLE t ALTER COLUMN c TYPE type USING c::type</c>, then <c>SET</c> or <c>DROP NOT
/// NULL</c> when <paramref name="NotNull"/> is given.
/// </summary>
public sealed record PgAlterColumn(PgName Table, string Column, string Type, bool? NotNull) : PgStatement;

/// <summary><c>ALTER TABLE t ALTER COLUMN c SET DEFAULT expr</c>, or <c>DROP DEFAULT</c> when <paramref name="Default"/> is null.</summary>
public sealed record PgSetDefault(PgName Table, string Column, PgExpr? Default) : PgStatement;

/// <summary><c>ALTER TABLE t {ENABLE | DISABLE} TRIGGER name</c>.</summary>
public sealed record PgEnableTrigger(PgName Table, string Trigger, bool Enable) : PgStatement;

/// <summary><c>DROP ROUTINE [IF EXISTS] name</c>: a procedure or a function (sql2pg never overloads a name).</summary>
public sealed record PgDropRoutine(PgName Name, bool IfExists) : PgStatement;

/// <summary><c>DROP TRIGGER [IF EXISTS] name ON table</c>.</summary>
public sealed record PgDropTrigger(string Name, PgName Table, bool IfExists) : PgStatement;

/// <summary>
/// <c>CREATE POLICY name ON table FOR command [USING (…)] [WITH CHECK (…)]</c>: a permissive
/// row-level security policy for one command (SELECT, INSERT, UPDATE or DELETE).
/// </summary>
public sealed record PgCreatePolicy(string Name, PgName Table, string Command, PgExpr? Using, PgExpr? WithCheck) : PgStatement;

/// <summary><c>DROP POLICY [IF EXISTS] name ON table</c>.</summary>
public sealed record PgDropPolicy(string Name, PgName Table, bool IfExists) : PgStatement;

/// <summary><c>ALTER TABLE table ENABLE | DISABLE | FORCE | NO FORCE ROW LEVEL SECURITY</c>.</summary>
public sealed record PgRowSecurity(PgName Table, string Action) : PgStatement;

/// <summary><c>ALTER SEQUENCE name RESTART [WITH value]</c>.</summary>
public sealed record PgRestartSequence(PgName Name, PgExpr? Value) : PgStatement;

/// <summary><c>CREATE ROLE name</c> (a role that cannot log in).</summary>
public sealed record PgCreateRole(string Name) : PgStatement;

/// <summary><c>DROP ROLE [IF EXISTS] name</c>.</summary>
public sealed record PgDropRole(string Name, bool IfExists) : PgStatement;

/// <summary><c>GRANT role TO member</c>, or <c>REVOKE role FROM member</c>.</summary>
public sealed record PgGrantRole(string Role, string Member, bool Revoke) : PgStatement;

/// <summary><c>ALTER ROLE name RENAME TO new_name</c>.</summary>
public sealed record PgRenameRole(string Name, string NewName) : PgStatement;

/// <summary>
/// The original T-SQL of something that could not be translated, kept in the output as a block
/// comment headed by its diagnostic code, at the place the translation would have gone.
/// </summary>
public sealed record PgUntranslated(string Code, string OriginalTsql) : PgStatement;

/// <summary>A line of explanation in the output: <c>-- text</c>.</summary>
public sealed record PgLineComment(string Text) : PgStatement;
