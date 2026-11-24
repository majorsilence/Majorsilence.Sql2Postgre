# Implementation plan

This is the build plan for Majorsilence.Sql2Postgre, written to be carried out one work package
at a time by an AI coding agent (Claude Sonnet 5.5) with a human reviewing each pull request.
[ROADMAP.md](../ROADMAP.md) explains *why* the tool is shaped this way; this document says
*what to build, in what order, and how to know it is done*. Where the two disagree, stop and
ask; do not pick one.

## How to use this document

1. Work packages (WP) are done **in order**, **one per pull request**, on a branch named
   `wp/<id>-<short-name>` (e.g. `wp/1.4-type-mapper`). Do not start a WP until the one before
   it is merged, unless its "Depends on" line says otherwise.
2. Before starting a WP, read: this section, [Hard rules](#hard-rules), [Contracts](#contracts),
   and the WP itself. Read the code the earlier WPs produced; don't re-derive it.
3. Do exactly what the WP says. If something it needs is missing or ambiguous, **stop and ask
   in the PR** rather than invent. Write down anything you learned that a later WP needs in
   the WP's "Notes for later" section of `BACKLOG.md`.
4. A WP is done only when every item in its **Done when** list holds *and* the
   [Definition of done](#definition-of-done) holds. Paste the test output summary in the PR.
5. Keep diffs to the WP's scope. Refactors of earlier code go in their own PR, with a reason.

### Commands

```bash
dotnet build Majorsilence.Sql2Postgre.slnx -c Release
dotnet test tests/Majorsilence.Sql2Postgre.Tests -c Release
dotnet test tests/Majorsilence.Sql2Postgre.Integration.Tests -c Release   # needs Docker (Linux containers)
dotnet test tests/Majorsilence.Sql2Postgre.Corpus.Tests -c Release        # needs Docker + scripts/download-corpus.sh
```

Run the test projects separately, as in majorsilence.crystal. Integration and corpus tests
are marked `[Category("Containers")]` and call `Assert.Ignore` with a clear message when Docker
is not reachable, so a machine without Docker still gets a green unit run.

## Hard rules

These apply to every WP. A PR that breaks one is not mergeable.

1. **Clean room.** Do not read, search for, copy, port or paraphrase the source, tests, mapping
   tables or documentation of any other SQL Server → PostgreSQL converter, transpiler, loader or
   compatibility layer (open source or commercial). The only sources of truth are:
   the Microsoft T-SQL reference documentation, the PostgreSQL manual, and the two engines
   themselves run in tests. If you need to know what T-SQL does, write a test that runs it on
   SQL Server. Package dependencies listed under [Dependencies](#dependencies) are allowed;
   adding any other package needs the reviewer's OK in the PR description.
2. **Pure, unmodified PostgreSQL output.** Everything emitted must run on stock community
   PostgreSQL 15–19 (see *Target version* below): core features, plus only these permitted extensions —

   | Extension | `CREATE EXTENSION` name | Used for |
   |---|---|---|
   | PostGIS | `postgis` | `geography`/`geometry` columns, spatial indexes and methods |
   | pgvector | `vector` | SQL Server 2025 `vector(n)` columns and `VECTOR_DISTANCE` |
   | pgcrypto | `pgcrypto` | `HASHBYTES` algorithms core lacks (`SHA1`), `CRYPT_GEN_RANDOM` |
   | pg_trgm | `pg_trgm` | Optional trigram indexes (Stage 4) |
   | pg_cron | `pg_cron` | Optional: SQL Server Agent T-SQL job steps (Stage 4) |
   | hstore | `hstore` | Permitted; no planned use |
   | uuid-ossp | `uuid-ossp` | Permitted; no planned use (`gen_random_uuid()` and, on 18, `uuidv7()` are core) |
   | pg_stat_statements | `pg_stat_statements` | Permitted; an operational tool, never emitted by the converter |

   Any other extension (`citext`, `ltree`, `tablefunc`/`crosstab`, …) is **not used unless the
   maintainer approves it**, case by case: propose it in an issue (what it would convert, what
   the core alternative is and why that falls short), and only after approval add it to this
   table and `PermittedExtensions.All` in its own PR. Never add one on your own judgment.
   Rules for the permitted ones: prefer a core construct whenever one does the job; emit `CREATE EXTENSION IF
   NOT EXISTS <name>;` (in the `Schemas` phase) only for extensions a converted object actually
   uses; each is gated by `ConversionOptions.Extensions`, and with an extension disabled the
   converter falls back to core types (e.g. spatial → `bytea`) or marks the object `Manual` —
   never fails. Every use records `S2P1401 ExtensionRequired` (Info) naming the extension.
   Helper functions are allowed only as plain `LANGUAGE sql` / `LANGUAGE plpgsql` functions
   that the generated script itself creates (the compat schema, WP 2.6). No C functions.
3. **Nothing is silently dropped.** Every construct the converter cannot translate produces a
   `Diagnostic` (see [Diagnostics](#diagnostics)), and the emitted SQL contains the original
   T-SQL in a `/* sql2pg:untranslated S2Pxxxx … */` comment at the place it was. An object with
   any `Error`-severity diagnostic is reported as `Manual`.
4. **Deterministic output.** Same input and options → byte-identical output. Sort everything
   that comes from a dictionary or the catalog by a stable key. No timestamps, GUIDs or machine
   names in generated SQL.
5. **Tests prove each rule.** Every translation rule has a golden test that fails without it.
   Rules about behaviour (what a function returns, how NULL propagates) also get a both-engines
   test (WP 0.3) when practical.
6. **Privacy.** Only the public corpus (WP 1.15) and schemas written in test code with made-up
   names enter the repo. Never commit a `.bak`, `.mdf` or connection string with a password.
7. **CHANGELOG.** Every PR adds an entry under `## Unreleased`: a bold sentence saying what now
   works, then a line on what happened before.
8. **Style.** `net10.0;net11.0`, nullable enabled, implicit usings, file-scoped namespaces,
   `sealed` records for data, no `static` mutable state. Comments say what SQL Server or
   PostgreSQL does and why the code does this; no history of attempts.

## Target version

**PostgreSQL 15–19, with 18 the default** (`TargetVersion`, default 18). From 2026-11-15 to
2026-11-19 the target was 18 alone; WP 4.17 then made the output work on 15, 16 and 17 as well,
and WP 4.20 added 19 (released 2026-11-19). New work targets 18 first and gives each 18-only form
a fallback (or a diagnostic) for 15–17, with a golden case at the older version; a 19-only form
is used only for 19, with the 18 form (or a diagnostic) otherwise. Whether 19 becomes the default
is the maintainer's decision. Container tests convert for the PostgreSQL they run on
(`SQL2PG_PG_VERSION`), and CI runs them on all five.

## Defaults for open decisions

ROADMAP.md lists five decisions. Until the maintainer says otherwise, implement these defaults
**as options** (in `ConversionOptions`, WP 1.1) so changing a default later is a one-line change:

| Decision | Default | Option |
|---|---|---|
| Identifier case | Fold to lower case, unquoted where legal | `NameStyle = Lower \| SnakeCase \| Preserve` |
| Case-insensitive collation | Report only (diagnostic `S2P1301` per CI text column) | `CaseInsensitive = Report \| IcuCollation` |
| Procedures returning rows | `FUNCTION … RETURNS TABLE` (single result set); `PROCEDURE` with `refcursor` params (several) | `ResultSetStyle = Function \| Refcursor` |
| Compat schema | On; schema name `mssql_compat` | `UseCompatSchema`, `CompatSchemaName` |
| Target PostgreSQL | 18 (see *Target version*); 15, 16 and 17 supported | `TargetVersion = 15..18` |
| `dbo` schema | Mapped to `public` | `SchemaMap` dictionary |
| Extensions | All permitted ones allowed; only those needed are emitted | `Extensions` set (empty = core only) |

## Repository layout

Create exactly this layout (WP 0.1 creates the shells):

```
Majorsilence.Sql2Postgre.slnx
Directory.Build.props            # Version, Authors, license, IsPackable=false default (as in majorsilence.crystal)
Directory.Build.targets          # packs LICENSE + README into packable projects
.editorconfig
AGENTS.md                        # short; points to this file and the hard rules
CHANGELOG.md  BACKLOG.md  ROADMAP.md  README.md  LICENSE
docs/IMPLEMENTATION_PLAN.md
docs/diagnostics.md              # one section per diagnostic code (WP 1.1 starts it)
scripts/download-corpus.sh       # WP 1.15
src/
  Majorsilence.Sql2Postgre.Model/
  Majorsilence.Sql2Postgre.SqlServer/        # catalog reader, script reader
  Majorsilence.Sql2Postgre.Translation/      # mappers, semantic model, translators, PgAst
  Majorsilence.Sql2Postgre.Postgres/         # PgWriter, ScriptPlanner, Applier
  Majorsilence.Sql2Postgre.Data/             # copy + verify
  Majorsilence.Sql2Postgre.Compat/           # embedded mssql_compat SQL (WP 2.6)
  Majorsilence.Sql2Postgre.Cli/              # `sql2pg` dotnet tool
tests/
  Majorsilence.Sql2Postgre.Tests/            # unit + golden tests, no Docker
    Golden/<area>/<case>.tsql, <case>.pgsql, [<case>.diag]
  Majorsilence.Sql2Postgre.Integration.Tests/  # Testcontainers: both engines
  Majorsilence.Sql2Postgre.Corpus.Tests/       # public sample databases end to end
.github/workflows/build.yml  .github/workflows/publish-nuget.yml
```

Project references (no others): `SqlServer → Model`; `Translation → Model`;
`Postgres → Model, Translation`; `Data → Model, Postgres`; `Compat → (none)`;
`Cli → all src`. Tests reference what they test.

## Dependencies

Use the latest **stable** (non-prerelease) version at implementation time. Versions verified
2026-11-14 are given for orientation.

| Package | Version | Where |
|---|---|---|
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.x | SqlServer, Translation |
| `Microsoft.Data.SqlClient` | 7.x | SqlServer, Data |
| `Npgsql` | 10.x | Postgres, Data |
| `System.CommandLine` | 3.x | Cli |
| `System.IO.Hashing` | 11.x | Data (XxHash64 for verification) |
| `NUnit`, `NUnit3TestAdapter`, `NUnit.Analyzers`, `Microsoft.NET.Test.Sdk`, `coverlet.collector` | latest stable | tests (if NUnit 5 gives adapter trouble, use the latest NUnit 4.x, as majorsilence.crystal does) |
| `Testcontainers.MsSql`, `Testcontainers.PostgreSql` | 4.x | Integration, Corpus tests |

`.dacpac` support and `Microsoft.SqlServer.DacFx` are **out of scope** for this plan.

## Contracts

These types are the spine every WP builds on. WP 1.1 creates them; later WPs extend them only
by adding members (never renaming) unless the PR says why.

### Model (`Majorsilence.Sql2Postgre.Model`)

```csharp
namespace Majorsilence.Sql2Postgre.Model;

public sealed record ObjectName(string Schema, string Name)        // source (SQL Server) names, as written
{
    public override string ToString() => $"{Schema}.{Name}";
}

public enum ObjectKind { Schema, Table, View, Function, Procedure, Trigger, Sequence, UserType, Index, Constraint, Synonym }

public sealed record SqlServerType(
    string Name,              // base type name, lower case: "nvarchar", "decimal", "datetime2"…
    int? Length,              // chars/bytes; -1 = max
    int? Precision,
    int? Scale,
    string? UserTypeName,     // alias type name when the column uses one
    string? Collation);

public sealed record Column(
    string Name, int Ordinal, SqlServerType Type, bool IsNullable,
    IdentitySpec? Identity, string? DefaultExpression, string? DefaultConstraintName,
    ComputedSpec? Computed, bool IsRowGuidCol, bool IsSparse);

public sealed record IdentitySpec(long Seed, long Increment);
public sealed record ComputedSpec(string Expression, bool IsPersisted);

public sealed record Table(ObjectName Name, IReadOnlyList<Column> Columns,
    PrimaryKey? PrimaryKey, IReadOnlyList<UniqueConstraint> Uniques,
    IReadOnlyList<ForeignKey> ForeignKeys, IReadOnlyList<CheckConstraint> Checks,
    IReadOnlyList<Index> Indexes, long? ApproximateRowCount);

public sealed record PrimaryKey(string Name, IReadOnlyList<IndexColumn> Columns, bool IsClustered);
public sealed record UniqueConstraint(string Name, IReadOnlyList<IndexColumn> Columns);
public sealed record ForeignKey(string Name, IReadOnlyList<string> Columns, ObjectName Referenced,
    IReadOnlyList<string> ReferencedColumns, ReferentialAction OnDelete, ReferentialAction OnUpdate,
    bool IsDisabled, bool IsNotTrusted);
public enum ReferentialAction { NoAction, Cascade, SetNull, SetDefault }
public sealed record CheckConstraint(string Name, string Expression, bool IsDisabled, bool IsNotTrusted);
public sealed record IndexColumn(string Name, bool Descending);
public sealed record Index(string Name, IReadOnlyList<IndexColumn> Keys, IReadOnlyList<string> Included,
    bool IsUnique, bool IsClustered, string? FilterExpression, string Type /* "rowstore", "columnstore", "xml", "spatial", "fulltext" */);

public sealed record Sequence(ObjectName Name, SqlServerType Type, long Start, long Increment,
    long? Min, long? Max, bool Cycle);

public sealed record UserType(ObjectName Name, SqlServerType BaseType, bool IsNullable, bool IsTableType,
    IReadOnlyList<Column> TableColumns);

public sealed record Module(ObjectName Name, ObjectKind Kind, string Definition,   // full CREATE text from sys.sql_modules
    ObjectName? ParentTable);                                                    // for triggers

public sealed record SourceDatabase(string Name, string DefaultCollation, int CompatibilityLevel,
    IReadOnlyList<string> Schemas, IReadOnlyList<Table> Tables, IReadOnlyList<Sequence> Sequences,
    IReadOnlyList<UserType> UserTypes, IReadOnlyList<Module> Modules,
    IReadOnlyList<(ObjectName From, ObjectName To)> Dependencies);
```

### Diagnostics

```csharp
public enum Severity { Info, Warning, Error }   // Error ⇒ object is Manual

public sealed record Diagnostic(string Code, Severity Severity, ObjectName? Object,
    string Message, SourceSpan? Span);

public sealed record SourceSpan(int Line, int Column, int Length);
```

Codes are `S2P` + four digits, grouped by area. Reserve ranges; each code is a `const string`
in `DiagnosticCodes` with an XML doc comment, and a section in `docs/diagnostics.md` saying what
it means and what a person should do.

| Range | Area |
|---|---|
| S2P0xxx | Input / reading / parsing |
| S2P1xxx | Types, names, collations |
| S2P2xxx | Schema objects (constraints, indexes, sequences) |
| S2P3xxx | Expressions and built-in functions |
| S2P4xxx | Queries and DML |
| S2P5xxx | Procedural code |
| S2P6xxx | Triggers |
| S2P7xxx | Data copy and verification |
| S2P8xxx | Application code (C#/VB.NET, Stage A) |
| S2P9xxx | Internal errors (a bug: the converter threw) |

### Options

```csharp
public sealed record ConversionOptions
{
    public int TargetVersion { get; init; } = 18;              // 15..18
    public NameStyle NameStyle { get; init; } = NameStyle.Lower;
    public CaseInsensitiveStrategy CaseInsensitive { get; init; } = CaseInsensitiveStrategy.Report;
    public ResultSetStyle ResultSetStyle { get; init; } = ResultSetStyle.Function;
    public bool UseCompatSchema { get; init; } = true;
    public string CompatSchemaName { get; init; } = "mssql_compat";
    public IReadOnlyDictionary<string, string> SchemaMap { get; init; } = new Dictionary<string, string> { ["dbo"] = "public" };
    public IReadOnlyDictionary<string, string> TypeOverrides { get; init; } = new Dictionary<string, string>(); // "schema.table.column" → PG type
    public IReadOnlyList<string> ExcludeObjects { get; init; } = [];   // "schema.name", wildcards allowed: "staging.*"
    public IReadOnlySet<string> Extensions { get; init; } = PermittedExtensions.All;  // subset of the permitted list
}

public static class PermittedExtensions
{
    public static readonly IReadOnlySet<string> All = new SortedSet<string>(StringComparer.Ordinal)
        { "hstore", "pg_cron", "pg_stat_statements", "pg_trgm", "pgcrypto", "postgis", "uuid-ossp", "vector" };
}
// Options validation rejects any name not in PermittedExtensions.All.
```

The CLI reads the same record from a JSON config file (`--config sql2pg.json`, System.Text.Json,
camelCase, enums as strings).

### Conversion result

```csharp
public enum ObjectStatus { Converted, ConvertedWithWarnings, Manual, Skipped }

public sealed record ConvertedObject(ObjectName Source, ObjectKind Kind, string TargetName,
    Phase Phase, string Sql, ObjectStatus Status, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ObjectName> DependsOn);

public enum Phase { Schemas, Types, Sequences, Tables, Compat, Code, Indexes, Constraints, ForeignKeys, Triggers }

public sealed record ConversionResult(IReadOnlyList<ConvertedObject> Objects, IReadOnlyList<Diagnostic> Diagnostics);
```

### Script files

`sql2pg schema --out <dir>` writes one file per phase, numbered in phase order
(`01-schemas.sql`, `02-types.sql`, `03-compat.sql`, `04-sequences.sql`, `05-tables.sql`,
`06-code.sql`, `07-indexes.sql`, `08-constraints.sql`, `09-foreign-keys.sql`,
`10-triggers.sql`) plus `report.json`. Each object starts with a marker line

```sql
-- sql2pg:object <kind> <target_schema>.<target_name> status=<status>
```

so the Applier (WP 1.9) can split a file back into objects and attribute errors. `06-code.sql`
starts with `SET check_function_bodies = off;` so PL/pgSQL bodies can be created in any order;
objects are still emitted in dependency order because views and `LANGUAGE sql` functions bind
at creation.

## Testing infrastructure

### Golden tests (WP 0.3)

`tests/Majorsilence.Sql2Postgre.Tests/Golden/<area>/<case>.tsql` is the input,
`<case>.pgsql` the exact expected output, and optional `<case>.diag` lists expected diagnostics
one per line as `S2P3001 Warning` (code and severity only; message text is not compared). An
optional first line in the `.tsql` file sets options:

```sql
-- options: {"targetVersion": 15, "nameStyle": "Preserve"}
```

`GoldenTests` discovers all cases with `[TestCaseSource]`, runs the translator, and compares
normalized output (line endings → `\n`, trailing whitespace trimmed). When the environment
variable `SQL2PG_UPDATE_GOLDEN=1` is set it rewrites the `.pgsql`/`.diag` files instead of
failing; a reviewer then reads the diff. Never set it in CI.

### Both-engines tests (WP 0.3)

```csharp
// In Integration.Tests. One SQL Server and one PostgreSQL container per test run (OneTimeSetUp),
// PostgreSQL version from SQL2PG_PG_VERSION (default 18). Images:
//   mcr.microsoft.com/mssql/server:2022-latest   postgres:<version>
await BothEngines.AssertSameResultAsync(
    tsql:   "SELECT LEN('ab  ') AS n",
    pgsql:  Translate("SELECT LEN('ab  ') AS n"),
    setup:  null);   // optional DDL/data run on both first (T-SQL, translated for PG)
```

`AssertSameResultAsync` runs each side, reads all rows into canonical strings (see
[Canonical values](#canonical-values)), and compares as multisets unless the query has a
top-level `ORDER BY`, then as sequences. Column names are compared case-insensitively.

CI runs the container tests on `ubuntu-latest` only, in a matrix over PostgreSQL 15/16/17/18.

### Canonical values

Used by both-engines tests and data verification so both sides are compared the same way:
`NULL` → `\N`; booleans and `bit` → `1`/`0`; integers → invariant decimal; `decimal`/`numeric`/
`money` → invariant, trailing zeros after the point removed, `-0` → `0`; `float`/`real` →
`R` round-trip format; date → `yyyy-MM-dd`; timestamps → `yyyy-MM-ddTHH:mm:ss.ffffff`;
`datetimeoffset`/`timestamptz` → UTC in the same format with `Z`; time → `HH:mm:ss.ffffff`;
`uniqueidentifier`/`uuid` → lower-case `D` format; binary → lower-case hex; strings → as is.

## Work packages

Sizes are rough guidance for review effort: S ≈ under 500 lines changed, M ≈ 500–1500, L ≈ more
(split an L if it can be split cleanly).

---

### Stage 0 — scaffolding

#### WP 0.1 — Solution skeleton (S)

**Goal:** an empty solution that builds, laid out as in [Repository layout](#repository-layout).

- Create the `.slnx`, all `src` and `tests` projects (empty `Class1.cs` is fine; delete it in
  the WP that adds real code), `Directory.Build.props`/`.targets` modelled on
  majorsilence.crystal's (same maintainer, same license: copying from it is allowed): Version
  `0.1.0`, Authors `Peter Gill`, Company `Majorsilence`, URLs pointing at this repository,
  `PackageLicenseFile=LICENSE`, `IsPackable=false` default, runtime-async on net11.
- Packable: Model, SqlServer, Translation, Postgres, Data, Compat, Cli. Cli is a tool:
  `PackAsTool`, `ToolCommandName=sql2pg`, `AssemblyName=sql2pg`.
- `AGENTS.md`: five lines — read `docs/IMPLEMENTATION_PLAN.md`; the hard rules (clean room;
  stock PostgreSQL plus only the permitted extensions; nothing dropped silently); run test projects separately; CHANGELOG
  entry per change.
- `CHANGELOG.md` with `## Unreleased`; `BACKLOG.md` with headings `## Done`, `## Measured`,
  `## Notes for later`.

**Done when:** `dotnet build Majorsilence.Sql2Postgre.slnx -c Release` succeeds with 0
warnings; `dotnet pack` produces seven `.nupkg` files.

#### WP 0.2 — CI and release (S)

- `.github/workflows/build.yml` modelled on majorsilence.crystal's: jobs `test` (ubuntu +
  windows, unit tests only), `containers` (ubuntu, matrix `pg: [15, 16, 17, 18]`, runs
  Integration tests with `SQL2PG_PG_VERSION`), `pack`, and `release` (draft GitHub release on
  `v*` tags). `publish-nuget.yml` pushes on release published with Trusted Publishing.
- Permissions minimal (`contents: read`; `contents: write` only on `release`).

**Done when:** the workflow runs green on the PR (container job may be green with zero tests).

#### WP 0.3 — Test harness (M)

- Unit project: `GoldenTests` runner as specified (it runs a placeholder
  `ITranslator` that echoes input until WP 2.2 replaces it; include one trivial case).
- Integration project: `SqlServerContainerFixture`, `PostgresContainerFixture`,
  `BothEngines.AssertSameResultAsync`, `CanonicalValue.Format(object?, string sourceTypeHint)`
  with unit tests for every rule in [Canonical values](#canonical-values), Docker guard with
  `Assert.Ignore("Docker is not available: …")`.
- One smoke test: `SELECT 1` on both engines through `AssertSameResultAsync` (hand-written
  PG side).

**Done when:** unit tests pass on Windows and Linux; container smoke test passes in CI for all
four PostgreSQL versions and is ignored (not failed) without Docker.

---

### Stage 1 — schema and data

#### WP 1.1 — Model, diagnostics, options (S)

Create every type in [Contracts](#contracts) (Model, Diagnostics, Options, Conversion result).
Start `docs/diagnostics.md` with the range table. Add `DiagnosticCodes` with
`S2P9001 InternalError` only; later WPs add codes as they need them.

**Done when:** builds; a unit test serializes `ConversionOptions` to JSON and back unchanged.

#### WP 1.2 — SQL Server catalog reader (L)

**Goal:** `SqlServerCatalogReader.ReadAsync(string connectionString, CancellationToken)` →
`SourceDatabase`, populated entirely from catalog views.

- One `const string` query per concern, against `sys.schemas`, `sys.tables`, `sys.columns`,
  `sys.types`, `sys.identity_columns`, `sys.default_constraints`, `sys.computed_columns`,
  `sys.key_constraints`, `sys.indexes`/`sys.index_columns`, `sys.foreign_keys`/
  `sys.foreign_key_columns`, `sys.check_constraints`, `sys.sequences`, `sys.table_types`,
  `sys.sql_modules` + `sys.objects` (views `V`, functions `FN`/`IF`/`TF`, procedures `P`,
  triggers `TR`), `sys.sql_expression_dependencies`, `sys.partitions` (row counts),
  `sys.databases` (collation, compatibility level). Exclude `is_ms_shipped = 1` objects.
- `nvarchar`/`nchar` lengths: `sys.columns.max_length` is bytes; divide by 2; `-1` stays `-1`.
- Objects whose module `definition` is `NULL` (encrypted) → still listed, `Definition = ""`,
  and diagnostic `S2P0101 EncryptedModule` (Error).
- CLR objects (`type` `FS`, `FT`, `PC`, `TA`) → diagnostic `S2P0102 ClrObject` (Error).
- All lists sorted by schema, then name (ordinal for columns).

**Tests (Integration):** a fixture script (in test code, made-up names: `shop.customer`,
`shop.order_line`, …) creating: every built-in type in a single table; identity with seed/
increment; named and unnamed defaults; persisted and non-persisted computed columns; PK
clustered and nonclustered; unique; FK with each referential action and one `NOCHECK`; check
constraints; filtered, included-column, descending and columnstore indexes; a sequence; an
alias type; a table type; a view, a scalar function, an inline TVF, a procedure, a trigger;
`WITH ENCRYPTION` procedure. Assert the model field by field.

**Done when:** that test passes; reader issues no queries other than the documented ones.

#### WP 1.3 — Script reader (M)

**Goal:** `SqlServerScriptReader.Read(IEnumerable<(string Path, string Text)>)` → `SourceDatabase`
for users without a live database (e.g. a folder of `CREATE` scripts).

- Parse with ScriptDom `TSql160Parser` (initialQuotedIdentifiers: true); split batches on `GO`.
- Build `Table`s from `CreateTableStatement` (plus `ALTER TABLE … ADD CONSTRAINT`, `CREATE
  INDEX`, `CREATE SEQUENCE`, `CREATE TYPE`); `Module`s from view/function/procedure/trigger
  statements (definition = the statement's script text, from the token stream).
- Parse errors → `S2P0001 ParseError` (Error) with span, file path in the message; continue with
  the next batch.
- Dependencies: leave empty here (WP 2.1 computes them from the AST for both readers).

**Tests (Unit):** the same fixture script as WP 1.2, read as text, produces a model equal to the
catalog reader's for every property scripts can express (document any property only the
catalog knows, e.g. row counts, in a test comment).

#### WP 1.4 — Name mapper (S)

**Goal:** `NameMapper` turns source names into PostgreSQL identifiers and remembers them.

- `NameStyle.Lower`: `ToLowerInvariant()`; `SnakeCase`: split on case changes and
  non-alphanumerics (`OrderID` → `order_id`, `HTTPServer` → `http_server`); `Preserve`: as is.
- Quote (`"…"`, doubling `"`) only when needed: not `[a-z_][a-z0-9_$]*` after mapping, or a
  PostgreSQL reserved keyword (embed the list of reserved keywords from the PostgreSQL manual's
  SQL Key Words appendix for version 18 as a resource; cite the appendix in a comment).
- Longer than 63 bytes (UTF-8) → truncate to 54 bytes + `_` + 8 hex of XxHash32 of the full
  name; diagnostic `S2P1001 IdentifierTruncated` (Warning).
- Collisions within one namespace (two tables in one schema, two columns in one table, index
  and constraint names within a schema — PostgreSQL puts indexes and tables in one namespace)
  → append `_2`, `_3`…; diagnostic `S2P1002 IdentifierCollision` (Warning).
- Schema mapping via `SchemaMap` happens first.
- API: `string Table(ObjectName)`, `string Column(ObjectName table, string column)`,
  `string Constraint(ObjectName table, string name)`, `string Index(…)`, `string Routine(…)`,
  `string Quote(string mapped)`; same input → same output for the whole run.

**Tests (Unit):** every style, reserved words (`user`, `order`, `select`), the 63-byte rule
(multibyte characters too), collisions, schema map, idempotence.

#### WP 1.5 — Type mapper (M)

**Goal:** `TypeMapper.Map(SqlServerType, ObjectName table, string column)` →
`(string PgType, IReadOnlyList<Diagnostic>)`, applying `TypeOverrides` first.

Mapping (exact strings; `n` = length, `p,s` = precision/scale):

| SQL Server | PostgreSQL | Diagnostic |
|---|---|---|
| `bit` | `boolean` | |
| `tinyint` | `smallint` | |
| `smallint` `int` `bigint` | `smallint` `integer` `bigint` | |
| `decimal(p,s)` `numeric(p,s)` | `numeric(p,s)` | |
| `money` / `smallmoney` | `numeric(19,4)` / `numeric(10,4)` | |
| `float(1..24)` / `real` | `real` | |
| `float(25..53)` / `float` | `double precision` | |
| `char(n)` `nchar(n)` | `char(n)` | |
| `varchar(n)` `nvarchar(n)` | `varchar(n)` | |
| `varchar(max)` `nvarchar(max)` `text` `ntext` `xml`-as-text never | `text` | |
| `binary(n)` `varbinary(n)` `varbinary(max)` `image` | `bytea` | |
| `date` | `date` | |
| `time(n)` | `time(min(n,6))` | `S2P1101 FractionalSecondsReduced` (Info) when n = 7 |
| `datetime` | `timestamp(3)` | |
| `smalldatetime` | `timestamp(0)` | |
| `datetime2(n)` | `timestamp(min(n,6))` | `S2P1101` when n = 7 |
| `datetimeoffset(n)` | `timestamptz(min(n,6))` | `S2P1102 OffsetNotPreserved` (Warning) |
| `uniqueidentifier` | `uuid` | |
| `xml` | `xml` | |
| `sysname` | `varchar(128)` | |
| `rowversion`/`timestamp` | `bytea` | `S2P1103 RowversionSemantics` (Warning) |
| `sql_variant` | `text` | `S2P1104 SqlVariantAsText` (Warning) |
| `hierarchyid` | `text` | `S2P1105 HierarchyIdAsText` (Warning) |
| `geography` / `geometry` with `postgis` enabled | `geography` / `geometry` | `S2P1401` (Info) |
| `geography` `geometry` without `postgis` | `bytea` (WKB) | `S2P1106 SpatialAsWkb` (Warning) |
| `vector(n)` (SQL Server 2025) with `vector` enabled | `vector(n)` | `S2P1401` (Info) |
| `vector(n)` without `vector` | `real[]` | `S2P1107 VectorAsArray` (Warning) |
| alias type | the domain's mapped name (WP 1.7 creates the domain) | |
| anything else | `text` | `S2P1199 UnknownType` (Error) |

`varchar(n)` with `n > 10485760` → `text`. A collation ending `_CI_*` on a text column →
`S2P1301 CaseInsensitiveCollation` (Info) when `CaseInsensitive = Report`.

**Tests (Unit):** one per row, plus overrides and the collation rule.
**Tests (Integration):** a table with one column per mapped type is created in PostgreSQL from
the mapped types for every target version.

#### WP 1.6 — Expression translator v0 (M)

**Goal:** translate the expressions that appear in schema objects — defaults, check
constraints, computed columns, filtered-index predicates — so Stage 1 DDL is complete. This is
the seed of the full translator in WP 2.2; build it in `Translation/Expressions/` with that
growth in mind (a ScriptDom-visitor that produces `PgAst` expression nodes, WP 1.7).

- Parse with `TSql160Parser.ParseExpression`/`ParseBooleanExpression`.
- Support: literals (strings `N'…'` → `'…'`, doubling quotes; numbers; `NULL`), redundant
  parentheses removed (SQL Server stores `((0))`), column references (mapped names), unary and
  binary arithmetic, comparisons, `AND`/`OR`/`NOT`, `IS [NOT] NULL`, `IN (list)`,
  `BETWEEN`, `LIKE` (no escape-char differences in this WP), `CASE`, `CAST`/`CONVERT` without
  style (target type via TypeMapper), and functions: `GETDATE()`/`CURRENT_TIMESTAMP` →
  `localtimestamp`, `SYSDATETIME()` → `localtimestamp`, `GETUTCDATE()`/`SYSUTCDATETIME()` →
  `(now() at time zone 'utc')`, `SYSDATETIMEOFFSET()` → `now()`, `NEWID()` →
  `gen_random_uuid()`, `NEWSEQUENTIALID()` → `uuidv7()` when `TargetVersion >= 18` (core,
  time-ordered), else `gen_random_uuid()` + `S2P3002` (Info: not sequential), `SUSER_SNAME()`/`SUSER_NAME()`/`USER_NAME()` → `current_user`, `ISNULL(a,b)` →
  `COALESCE(a,b)`, `UPPER`, `LOWER`, `LTRIM`, `RTRIM`, `ABS`, `ROUND(x,n)`.
- `bit` columns: comparisons with `1`/`0` become `true`/`false`; a default `((1))` on a
  `boolean` column becomes `true`. The translator needs column types for this: take a
  `Func<string, SqlServerType?> columnType` for now (WP 2.1 replaces it with the semantic model).
- `+` with a string-typed operand (literal or known column) → `||`; otherwise `+`.
- Anything else → `S2P3001 UnsupportedExpression` (Error) and the caller drops that default/
  check/computed column definition, keeps the original in an untranslated comment.

**Tests (Golden):** `Golden/expressions-v0/` — at least one case per bullet.
**Tests (Both engines):** each supported function's value on a fixed input matches
(`ROUND(2.5, 0)`, `LTRIM('  a')`, `ISNULL(NULL, 1)` …).

#### WP 1.7 — PgAst and PgWriter for DDL (M)

**Goal:** a small immutable tree for the PostgreSQL we emit, and a writer that prints it.

- `Translation/PgAst/`: records for `CreateSchema`, `CreateDomain`, `CreateCollation`,
  `CreateType` (composite), `CreateSequence`, `CreateTable` (columns: name, type, not null,
  default, identity `GENERATED BY DEFAULT AS IDENTITY (START WITH … INCREMENT BY …)`,
  generated `GENERATED ALWAYS AS (…) STORED|VIRTUAL`), `AddPrimaryKey`, `AddUnique`,
  `AddCheck` (with `NOT VALID`), `AddForeignKey` (actions, `NOT VALID`), `CreateIndex`
  (unique, desc, `INCLUDE`, `WHERE`), `Comment` (for untranslated blocks), and expression
  nodes from WP 1.6.
- `Postgres/PgWriter.cs`: `string Write(PgNode, int targetVersion)`. Formatting: keywords upper
  case, 4-space indent, one column per line, statements end with `;\n`.
- Version rules: `VIRTUAL` generated columns only when `targetVersion >= 18`, else `STORED`
  with `S2P2001 GeneratedColumnStored` (Info) for non-persisted computed columns.
- Untranslated blocks: `/* sql2pg:untranslated S2Pxxxx\n<original>\n*/`; any `*/` inside the
  original is written as `* /`.

**Tests (Unit):** a writer test per node and per version rule.

#### WP 1.8 — DDL translator and script planner (L)

**Goal:** `SchemaTranslator.Translate(SourceDatabase, ConversionOptions)` → `ConversionResult`
for everything except modules (modules are listed as `Skipped` with `S2P0201 NotYetSupported`
(Info) until Stage 2/3 converts them).

- Phases as in [Script files](#script-files). Tables are created with columns, defaults,
  identity, generated columns and `NOT NULL` only; PK/unique/check go in `Constraints`, FKs in
  `ForeignKeys`, indexes in `Indexes` — so data loads before them.
- Clustered PK/indexes → plain B-tree; `S2P2002 ClusteredIgnored` (Info) once per table.
- Spatial indexes → `CREATE INDEX … USING gist (col)` when `postgis` is enabled.
- Columnstore, XML, full-text indexes (and spatial without `postgis`) →
  `S2P2003 IndexTypeUnsupported` (Warning), not emitted.
- Extensions: collect the extensions every converted object needs and emit one `CREATE
  EXTENSION IF NOT EXISTS` each, sorted by name, at the top of `01-schemas.sql`.
- Disabled or not-trusted FK/check → `NOT VALID` (`S2P2004`, Info).
- `IsRowGuidCol`, sparse columns → ignored with Info diagnostics.
- Alias types → `CREATE DOMAIN`; table types → `CREATE TYPE … AS (…)` (`S2P2005`, Info:
  table-valued parameters need manual review).
- Sequences → `CREATE SEQUENCE … AS <type>`.
- Identity: emit `GENERATED BY DEFAULT` (so the data load can insert explicit values).
- Schemas: one `CREATE SCHEMA IF NOT EXISTS` per mapped schema except `public`.
- `IcuCollation` option: emit once `CREATE COLLATION IF NOT EXISTS sql2pg_ci (provider = icu,
  locale = 'und-u-ks-level2', deterministic = false);` in `Types` and add `COLLATE sql2pg_ci` to
  CI text columns; if `TargetVersion < 18` add `S2P1302 LikeOnNondeterministic` (Warning) for
  each such column (LIKE and substring searches are unsupported there before 18; WP 4.17 rewrites
  LIKE on it).
- `ScriptPlanner`: orders objects by phase, then topologically by `DependsOn` inside a phase
  (ties by name), and writes the phase files + `report.json` (`ConversionResult` serialized).
  A dependency cycle → emit in name order and `S2P0202 DependencyCycle` (Warning).

**Tests (Golden):** `Golden/ddl/` — one case per bullet, input is a `CREATE TABLE` script read
through the WP 1.3 reader.
**Tests (Integration):** the WP 1.2 fixture database is read, translated and applied (WP 1.9)
to PostgreSQL 15–18 with zero errors.

#### WP 1.9 — Applier (M)

**Goal:** `PgApplier.ApplyAsync(IEnumerable<ConvertedObject> or script directory, string
pgConnectionString)` → `ApplyReport` (per object: ok / error message / SQLSTATE).

- Each object runs in its own transaction; an error is recorded and the run continues.
  Option `--stop-on-error`.
- Script directory input is split on `-- sql2pg:object` markers.
- Objects with status `Manual` are skipped unless `--apply-manual`.
- `--recreate` drops and recreates the target database first (requires a maintenance
  connection; never default).

**Tests (Integration):** a script with one good and one deliberately broken object reports one
success, one failure with SQLSTATE, and does not stop.

#### WP 1.10 — Data copier (L)

**Goal:** `DataCopier.CopyAsync(SourceDatabase, ConversionResult, string sqlConn, string pgConn,
DataCopyOptions, IProgress<TableProgress>)` copies every converted table.

- Per table, build a source `SELECT` listing columns explicitly, with per-type source
  expressions: `hierarchyid` → `CAST(col AS nvarchar(4000))`, `geography`/`geometry` →
  `col.STAsBinary()` (plus `col.STSrid` when the target is a PostGIS column), `sql_variant` →
  `CAST(col AS nvarchar(4000))`, `xml` → `CAST(col AS nvarchar(max))`, `vector` →
  `CAST(col AS nvarchar(max))` (JSON array text); skip computed columns (PostgreSQL computes
  generated columns itself).
- PostGIS columns: write EWKB built from the WKB and SRID (set the `0x20000000` SRID flag in
  the geometry type word, insert the 4-byte SRID after it), which PostGIS's binary input
  accepts. A value with Z or M (`col.HasZ`/`HasM`) → `S2P7002 SpatialZmDropped` (Warning).
- `vector` columns: parse the JSON array and write with the text form `[1,2,3]` through a
  per-column text conversion, or as `real[]` when `vector` is disabled.
- Read with `CommandBehavior.SequentialAccess`; write with `NpgsqlBinaryImporter`
  (`COPY … (cols) FROM STDIN (FORMAT BINARY)`), passing explicit `NpgsqlDbType`s.
- Value transforms: `bit` → `bool`; `tinyint` → `short`; `datetime*` → `DateTime` with
  `DateTimeKind.Unspecified`; `datetimeoffset` → `.UtcDateTime` as UTC for `timestamptz`;
  `time` → `TimeSpan`; `datetime2(7)`/`time(7)` → round to microseconds (same rounding as the
  PostgreSQL server: half away from zero — confirm with a both-engines test); strings
  containing `\0` → remove the character and count it (`S2P7001 NulCharacterRemoved`, Warning,
  once per column with the count).
- Parallelism: `MaxParallelTables` (default 4). A table with a single-column integer PK and
  more than `SplitThresholdRows` (default 5,000,000) rows is split into `MaxParallelTables`
  key ranges copied concurrently.
- After all tables: for each identity column `SELECT setval(pg_get_serial_sequence(…),
  COALESCE(max(col), seed), max(col) IS NOT NULL)`; for each sequence, `setval` to SQL Server's
  `current_value`; then `ANALYZE`.
- Resume: a state file (`--state sql2pg-state.json`) records finished tables/ranges; a rerun
  skips them, and truncates and recopies an unfinished one.

**Tests (Integration):** the all-types table with edge values (min/max of each type, `NULL`s,
empty strings, `\0`, 7-digit fractions, offsets ±14:00, max-length strings, 10 MB blob)
copies; every value read back from PostgreSQL equals the canonical form of the source value
after the documented transforms. A split copy of a 100,000-row table gives the same checksum
as a single-stream copy.

#### WP 1.11 — Data verifier (M)

**Goal:** `DataVerifier.VerifyAsync(…)` → per table: source rows, target rows, checksum match.

- Row counts: `COUNT_BIG(*)` vs `count(*)`.
- Checksum (opt-in `--verify checksum`): stream every row from both sides (any order), format
  each value canonically, hash each row with XxHash64 over the UTF-8 of the values joined by
  `\u001f`, and keep the count and the **sum modulo 2^64** of the row hashes. Equal (count, sum)
  ⇒ the multisets agree. Order-independence matters: the two engines sort strings differently.
- Mismatch → `S2P7101 RowCountMismatch` / `S2P7102 ChecksumMismatch` (Error).

**Tests (Integration):** equal tables verify; changing one value in PostgreSQL makes the
checksum fail; deleting one row makes the count fail.

#### WP 1.12 — CLI v1 (M)

**Goal:** `sql2pg` with verbs `assess`, `schema`, `data`, `migrate`, `apply` using
`System.CommandLine`.

```
sql2pg assess  --source <conn | dir> [--config f.json] [--json report.json]
sql2pg schema  --source <conn | dir> --out <dir> [--config f.json]
sql2pg apply   --scripts <dir> --target <pgconn> [--stop-on-error] [--apply-manual]
sql2pg data    --source <conn> --target <pgconn> [--tables a,b] [--parallel 4] [--state f] [--verify count|checksum]
sql2pg migrate --source <conn> --target <pgconn> [--config f.json] [--verify count|checksum]
```

- `--source` is a connection string if it contains `=`, else a directory of `.sql` files.
- Connection strings may also come from `SQL2PG_SOURCE` / `SQL2PG_TARGET` environment
  variables; never print a connection string (mask `Password=`).
- `migrate` = schema phases Schemas..Compat → data → Code..Triggers → verify.
- `assess` prints a summary table (objects by kind × status, top 20 diagnostic codes by count)
  and writes the JSON report.
- Exit codes: 0 success, 1 completed with `Manual` objects or verification failures, 2 usage
  error, 3 unexpected error.

**Tests (Unit):** argument parsing, exit codes, password masking.
**Tests (Integration):** `migrate` of the WP 1.2 fixture database succeeds end to end.

#### WP 1.13 — Public corpus and compile scan (M)

**Goal:** measure Stage 1 on real schemas.

- `scripts/download-corpus.sh`: downloads into `tests/corpus/` (git-ignored) from
  `github.com/microsoft/sql-server-samples` releases and folders: AdventureWorks2022 (OLTP)
  and AdventureWorksDW2022 `.bak`, WideWorldImporters-Full and WideWorldImporters DW `.bak`,
  and `samples/databases/northwind-pubs/instnwnd.sql` and `instpubs.sql`. Verify SHA-256 of
  each download against values recorded in the script.
- Corpus test fixture restores each `.bak` into the SQL Server container (`CopyAsync` into
  `/var/opt/mssql/backup`, `RESTORE FILELISTONLY` then `RESTORE DATABASE … WITH MOVE`) or runs
  the install script.
- `CompileScanTests`: for each corpus database, `migrate` into a fresh PostgreSQL database and
  record: objects by status, apply failures (must be 0 for non-`Manual` objects), row-count and
  checksum verification (must all pass).
- **Positive control:** a test that injects a deliberately wrong type mapping (e.g.
  `int → date`) via `TypeOverrides` for one column and asserts the scan *reports* the failure.
  If the control passes silently, the scan is broken.
- Record results in `BACKLOG.md` under `## Measured` (counts only).

**Done when:** all corpus databases' Stage 1 objects apply with 0 errors and 100% data
verification on PostgreSQL 15–18; the control is caught. **This completes v0.1.**

---

### Stage 2 — queries, expressions and views

#### WP 2.1 — Semantic model (L)

**Goal:** `SemanticModel` answers "what is this name and what type is this expression" for the
translators.

- Symbol table from `SourceDatabase`: tables/views (columns + types), routines (parameters,
  return type, result columns for TVFs), user types, sequences, synonyms.
- Scopes: query scopes (FROM-clause aliases, CTEs, derived tables, `APPLY`), routine scopes
  (parameters, `DECLARE`d variables, table variables, `#temp` tables created earlier in the
  body), resolved inside-out as SQL Server does.
- `SqlServerType? TypeOf(ScalarExpression)`: literals, columns, variables, parameters, `CAST`/
  `CONVERT`, arithmetic with SQL Server data type precedence (from the "Data type precedence"
  page of the T-SQL reference), `CASE` (highest precedence of branches), built-in function
  return types (table maintained beside `FunctionMap`, WP 2.2), scalar UDF return types.
  Unknown → `null`, never a guess.
- View result columns are inferred from their `SELECT` so views can be used as sources.
- Dependencies: walk each module's AST for referenced objects; this fills
  `SourceDatabase.Dependencies` for the script reader and supplements the catalog's.
- Cross-database (`otherdb.dbo.t`) or linked-server (`srv.db.dbo.t`) names →
  `S2P0301 ExternalReference` (Error).

**Tests (Unit):** resolution and typing cases for each bullet, including a column shadowed by a
derived-table alias and a variable shadowed in a nested block.

#### WP 2.2 — Expression translator and function map (L)

**Goal:** full scalar-expression translation, replacing v0's limited set and `columnType`
callback with `SemanticModel`.

- `FunctionMap`: one entry per T-SQL built-in, each with `(name, arity, translate, returnType)`
  and a doc comment quoting nothing — describe behaviour in your own words. Required entries
  (with the translation to implement):

  | T-SQL | PostgreSQL |
  |---|---|
  | `LEN(s)` | `length(rtrim(s))` |
  | `DATALENGTH(x)` | `octet_length(x)` |
  | `CHARINDEX(f, s)` / `(f, s, start)` | `strpos(s, f)` / compat `charindex` |
  | `SUBSTRING(s, a, n)` | `substring(s from a for n)` (both-engines test negative/zero `a`) |
  | `LEFT`, `RIGHT`, `REPLACE`, `REVERSE`, `REPLICATE(s,n)` | `left`, `right`, `replace`, `reverse`, `repeat` |
  | `STUFF(s, a, n, r)` | `overlay(s placing r from a for n)` |
  | `CONCAT(...)` | `concat(...)` |
  | `CONCAT_WS` | `concat_ws` |
  | `STRING_AGG(x, sep) [WITHIN GROUP (ORDER BY …)]` | `string_agg(x, sep [ORDER BY …])` |
  | `TRIM`, `LTRIM`, `RTRIM` (incl. 2022 char-set forms) | `btrim`/`ltrim`/`rtrim` |
  | `IIF(c, a, b)` | `CASE WHEN c THEN a ELSE b END` |
  | `CHOOSE(i, a, b, …)` | `CASE i WHEN 1 THEN a … END` |
  | `NULLIF`, `COALESCE` | same |
  | `ISNUMERIC(x)` | compat `isnumeric` |
  | `TRY_CAST(x AS t)` / `TRY_CONVERT` | compat `try_cast_<type>` (generated per target type) |
  | `CONVERT(t, x, style)` | `to_char`/`to_timestamp`/`to_date` per style; unsupported style → `S2P3003` (Error) |
  | `FORMAT(x, fmt)` | `to_char` for the documented standard formats; others `S2P3004` (Error) |
  | `DATEADD(part, n, d)` | `d + n * interval '1 <part>'` |
  | `DATEDIFF(part, a, b)` | compat `datediff(part, a, b)` (boundary-crossing count, as SQL Server) |
  | `DATEPART(part, d)`, `YEAR`, `MONTH`, `DAY` | `extract(…)::int`; `weekday`/`week` via compat (depend on `@@DATEFIRST`: assume 7, `S2P3005` Info) |
  | `DATENAME(part, d)` | `to_char` (`'FMMonth'`, `'FMDay'`) or `extract::text` |
  | `EOMONTH(d [, n])` | `(date_trunc('month', d) + interval '1 month' * (n+1) - interval '1 day')::date` |
  | `DATEFROMPARTS`, `DATETIMEFROMPARTS` | `make_date`, `make_timestamp` |
  | `CAST(d AS date)` etc. | `CAST` with mapped type |
  | `ABS`, `CEILING`, `FLOOR`, `POWER`, `SQRT`, `SQUARE(x)`, `SIGN`, `EXP`, `LOG(x)`, `LOG10`, `PI`, `RAND()` | `abs`, `ceil`, `floor`, `power`, `sqrt`, `x*x`, `sign`, `exp`, `ln(x)`, `log(x)`, `pi`, `random()` |
  | `ROUND(x, n)` / `ROUND(x, n, 1)` | `round(x, n)` / `trunc(x, n)` |
  | `@@ROWCOUNT`, `@@ERROR`, `SCOPE_IDENTITY()` | handled by WP 3.x (procedural); in a plain query → `S2P3006` (Error) |
  | `@@SPID`, `DB_NAME()`, `HOST_NAME()`, `APP_NAME()` | `pg_backend_pid()`, `current_database()`, `inet_client_addr()::text`, `current_setting('application_name')` |
  | `NEWID`, `GETDATE`, … | as in v0 |
  | `HASHBYTES('MD5'\|'SHA2_256'\|'SHA2_512', x)` | core: `decode(md5(x), 'hex')`, `sha256(x)`, `sha512(x)` (on `convert_to(x, 'UTF8')` for text; note SQL Server hashes `nvarchar` as UTF-16 → `S2P3008`, Warning) |
  | `HASHBYTES('SHA1', x)` | `digest(x, 'sha1')` (`pgcrypto`); without it `S2P3009` (Error) |
  | `CRYPT_GEN_RANDOM(n)` | `gen_random_bytes(n)` (`pgcrypto`); without it `S2P3009` (Error) |
  | `VECTOR_DISTANCE('cosine'\|'euclidean'\|'dot', a, b)` | `a <=> b`, `a <-> b`, `-(a <#> b)` (`vector`); without it `S2P3009` (Error) |
  | spatial methods (`STDistance`, `STIntersects`, `STAsText`, `geography::Point`, …) | `S2P3030 SpatialMethod` (Error) until the Stage 4 PostGIS mapping |
  | `JSON_VALUE(j, '$.a.b')`, `JSON_QUERY`, `ISJSON`, `OPENJSON` (default schema) | `j::jsonb #>> '{a,b}'`, `#>`, compat `isjson`, `jsonb_each`; paths that are not simple → `S2P3007` (Error) |

- Operators: `+` → `||` when either side's type is a string type (per `SemanticModel`); when
  unknown, `+` stays and `S2P3010 AmbiguousPlus` (Warning). `%` → `%`. Integer division is the
  same in both engines; keep it. String comparison and `LIKE`: `[abc]`/`[^abc]` character
  classes in `LIKE` patterns → `SIMILAR TO` or `~` with an equivalent regex (literal patterns
  only; otherwise `S2P3011`, Error). `LIKE` escape: SQL Server has none by default, PostgreSQL
  uses `\` — emit `ESCAPE ''` when the pattern contains `\`.
- Implicit conversions SQL Server makes and PostgreSQL rejects (string literal compared with a
  number, number concatenated to a string) → explicit `CAST` on the side SQL Server would
  convert (by precedence).
- Every compat function used is recorded on the `ConvertedObject` so `03-compat.sql` contains
  only what is needed.

**Tests:** a golden case per table row and per operator rule; a both-engines test per function
over a small value grid including `NULL`, empty string, trailing spaces, negative numbers,
month ends and leap days.

#### WP 2.3 — Query translator (L)

**Goal:** `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE` statements to PostgreSQL.

- `SELECT`: `TOP (n)` → `LIMIT n`; `TOP (n) WITH TIES` → `FETCH FIRST n ROWS WITH TIES`
  (requires `ORDER BY`); `TOP (n) PERCENT` → `LIMIT (SELECT ceil(count(*) * n / 100.0) FROM
  (<query without top>) q)` (`S2P4001`, Info: evaluates the query twice); `OFFSET … FETCH`
  → same; `SELECT @v = …` is procedural (WP 3.2); `SELECT … INTO newtable` → `CREATE TABLE …
  AS` (`#temp` → `CREATE TEMP TABLE … AS`); bracketed/quoted names → mapped names; table
  hints (`WITH (NOLOCK)` …) and `OPTION (…)` → dropped, `S2P4002` (Info); `CROSS APPLY` →
  `CROSS JOIN LATERAL`; `OUTER APPLY` → `LEFT JOIN LATERAL … ON true`; CTEs → `WITH`, and
  `WITH RECURSIVE` when a CTE references itself; `UNION`/`EXCEPT`/`INTERSECT` same; window
  functions same; `GROUP BY ROLLUP/CUBE/GROUPING SETS` same; `ORDER BY` with `NULL`s: SQL
  Server sorts NULLs first ascending, PostgreSQL last → add `NULLS FIRST` to ascending and
  `NULLS LAST` to descending keys (both-engines test).
- `INSERT`: `OUTPUT inserted.c` → `RETURNING c`; `OUTPUT … INTO @t` is procedural (WP 3.5);
  `INSERT … EXEC` → `S2P4003` (Error) here, handled in WP 3.4; `DEFAULT VALUES` same.
- Identity inserts: `SET IDENTITY_INSERT t ON` → dropped (identity is `BY DEFAULT`); after the
  batch emit a `setval` and `S2P4004` (Info).
- `UPDATE t SET … FROM t JOIN u …` → `UPDATE t SET … FROM u WHERE <join condition>` (rewrite
  the join; when the target is aliased in `FROM`, use the alias as target). `DELETE t FROM t
  JOIN u` → `DELETE FROM t USING u WHERE …`. `OUTPUT deleted.*` → `RETURNING`.
- `UPDATE … SET @v = col = expr` (quirky update) → `S2P4005` (Error).
- `MERGE` → `MERGE` (target ≥ 15). `OUTPUT` on `MERGE` → `RETURNING` only when target ≥ 17,
  else `S2P4006` (Error). `WHEN NOT MATCHED BY SOURCE` → `WHEN NOT MATCHED BY SOURCE` on
  target ≥ 17, else `S2P4007` (Error).
- `FOR XML PATH('')` used to concatenate (`STUFF((SELECT ',' + x … FOR XML PATH('')), 1, 1, '')`
  and the `, TYPE).value('.', …)` variant) → `string_agg`. Other `FOR XML` → `S2P4010` (Error).
- `FOR JSON PATH/AUTO` (flat, with `ROOT`, `INCLUDE_NULL_VALUES`, `WITHOUT_ARRAY_WRAPPER`)
  → `json_agg(json_build_object(…))`; nested paths (`'a.b'` aliases) → nested
  `json_build_object`. Others `S2P4011` (Error).
- `PIVOT` → `SELECT … max(CASE WHEN col = 'v' THEN val END) AS v … GROUP BY …`; `UNPIVOT` →
  `CROSS JOIN LATERAL (VALUES ('a', a), ('b', b)) u(name, val) WHERE val IS NOT NULL`.

**Tests:** golden case per bullet; both-engines tests over a small fixture schema for `TOP`
variants, `APPLY`, `UPDATE … FROM`, `MERGE`, `PIVOT`, `FOR XML PATH` concatenation, NULL
ordering.

#### WP 2.4 — Views (M)

- `CREATE VIEW` → `CREATE VIEW` (or `CREATE OR REPLACE VIEW` with `--replace`).
- `WITH SCHEMABINDING` → dropped (Info). `WITH CHECK OPTION` → same.
- Indexed views (a view with a unique clustered index) → `CREATE MATERIALIZED VIEW` + indexes +
  `S2P2010 MaterializedViewRefresh` (Warning: must be refreshed; SQL Server kept it current).
- `SELECT *` in a view → expand to the columns at conversion time (PostgreSQL does this too, but
  explicit output is reviewable).
- Views that `ORDER BY` with `TOP 100 PERCENT` → drop both (SQL Server ignores that order too;
  `S2P4012`, Info).

**Done when:** every view in the corpus databases converts and applies, or is `Manual` with a
specific code (not `S2P9001`).

#### WP 2.5 — `translate` verb and ad-hoc SQL (S)

- `sql2pg translate <file-or-dir> [-o <outdir>] [--source <conn|dir>]` translates arbitrary
  T-SQL scripts. `--source` supplies the schema for the semantic model; without it, types are
  unknown and the converter says so (`S2P3010` etc. more often).
- `GO` separators, `USE db` (→ `\c`-free: dropped with Info), `SET` session options (dropped
  with Info) handled.
- Output mirrors input file names with `.pgsql` extension and a `.diag.json` beside each.

#### WP 2.6 — Compat schema (M)

**Goal:** `Majorsilence.Sql2Postgre.Compat` holds one `.sql` file per helper as an embedded
resource, in plain `LANGUAGE sql` (preferred) or `plpgsql`, `IMMUTABLE`/`STABLE` as
appropriate, created in `CompatSchemaName`.

- Functions required by WP 2.2: `charindex(text, text, int)`, `isnumeric(text)`,
  `datediff(text, timestamp, timestamp)` (+ `date` and `timestamptz` overloads),
  `datepart_week`, `datepart_weekday`, `isjson(text)`, `try_cast_int(text)`,
  `try_cast_numeric(text)`, `try_cast_date(text)`, `try_cast_timestamp(text)`.
- Behaviour of each is established by both-engines tests over a value grid; write the expected
  behaviour in the function's header comment in your own words.
- When `UseCompatSchema = false`, every use becomes `S2P3020 CompatDisabled` (Error).

**Done when:** each helper's both-engines grid passes on PostgreSQL 15–18.

#### WP 2.7 — Result-agreement harness (M)

- `tests/Majorsilence.Sql2Postgre.Corpus.Tests/Queries/<database>/*.sql`: T-SQL queries written
  for this repo against the public corpus schemas (aim for 30 per database, covering joins,
  aggregates, dates, strings, `TOP`, `APPLY`, CTEs, window functions).
- For each: translate, run on both (SQL Server corpus DB and the migrated PostgreSQL DB),
  compare with `BothEngines` rules, score = matching rows / total rows × 100 (100 when equal).
- `Baselines.json` records the score per query; a drop of more than 0 or a rise of more than 5
  fails until the baseline is updated in the same PR with a reason.

**Done when:** harness runs in CI's container job; baselines recorded in `BACKLOG.md`. **This
completes v0.2.**

---

### Stage 3 — procedural code

Naming rules for all of Stage 3:

- Parameters keep their name without `@`, mapped by `NameMapper` (so callers using named
  arguments still work). In the body they are referenced as `<routine_name>.<param>` so they
  never clash with column names.
- Local variables become `v_<name>`; the body starts with `#variable_conflict use_column`
  (a bare name is always a column, as in T-SQL, where variables always carry `@`).
- Every body is a single outer `BEGIN … END` block; all `DECLARE`s are hoisted to it, in
  order of first declaration. A variable declared twice in different branches with different
  types → `S2P5001` (Error).

#### WP 3.1 — PL/pgSQL AST and writer (M)

Nodes: `Block` (label, declarations, statements, exception handlers), `Declare` (name, type,
default), `Assign`, `If`/`ElsIf`/`Else`, `While`, `Loop`, `Exit`/`Continue` (with label),
`ForQuery`, `Return`, `ReturnQuery`, `ReturnNext`, `Raise` (level, format, args, `USING
ERRCODE`), `Perform`, `ExecuteDynamic` (`EXECUTE … INTO … USING …`), `GetDiagnostics`,
`OpenCursor`/`Fetch`/`Close`, `Commit`/`Rollback`, `Call`, `SqlStatement` (wraps a translated
DML statement, optional `INTO`). Writer prints with dollar quoting `$$` (or `$body$` if `$$`
occurs in the body).

#### WP 3.2 — Statements and control flow (L)

- `DECLARE @x int = 1` → declaration + default; `SET @x = e` → `v_x := e;`;
  `SELECT @x = a, @y = b FROM …` → `SELECT a, b INTO v_x, v_y FROM … LIMIT 1` — but SQL Server
  assigns from the *last* row and keeps the old value on zero rows: emit `SELECT … INTO` only
  when the query provably returns ≤ 1 row (PK/unique equality or aggregate without `GROUP
  BY`), else `S2P5002` (Warning) and add `LIMIT 1`. Zero rows: PL/pgSQL sets `NULL`; SQL Server
  keeps the old value — wrap as `SELECT … INTO tmp …; IF FOUND THEN v_x := tmp; END IF;` when
  the variable was previously assigned.
- `IF … ELSE`, `BEGIN … END`, `WHILE`, `BREAK` → `EXIT`, `CONTINUE`, `RETURN`.
- `PRINT e` → `RAISE NOTICE '%', e;`.
- `RAISERROR(msg, sev, state, args…)`: severity ≤ 10 → `RAISE NOTICE`; > 10 → `RAISE
  EXCEPTION` with `%s`/`%d` placeholders → `%`. `THROW n, msg, state` → `RAISE EXCEPTION
  USING MESSAGE = msg, ERRCODE = 'P0001', DETAIL = 'mssql_error=' || n`. Bare `THROW` in a
  `CATCH` → `RAISE;`.
- `SET NOCOUNT ON/OFF`, `SET XACT_ABORT ON`, `SET ANSI_*` → dropped. `XACT_ABORT OFF` (or
  default) with statements after an error-prone statement and no `TRY` → `S2P5003` (Info:
  PostgreSQL aborts on the first error).
- `@@ROWCOUNT` → `GET DIAGNOSTICS v_rowcount = ROW_COUNT;` inserted right after the statement
  whose count is read; `@@ERROR` → `0` inside straight-line code with `S2P5004` (Warning), or
  use inside `CATCH` → `1`.
- `SCOPE_IDENTITY()` / `@@IDENTITY` after an `INSERT` → `RETURNING <identity col> INTO
  v_scope_identity` on that insert.
- `WAITFOR DELAY 'hh:mm:ss'` → `PERFORM pg_sleep(<seconds>)`. `GOTO`/labels → `S2P5005`
  (Error).

#### WP 3.3 — Functions (M)

- Scalar function → `CREATE FUNCTION f(params) RETURNS <type> LANGUAGE plpgsql`; a body that is
  a single `RETURN <expr>` → `LANGUAGE sql` with `RETURN <expr>` / `SELECT <expr>`.
  `STABLE` when it reads tables, `IMMUTABLE` when it reads nothing and calls only immutable
  functions, else `VOLATILE`.
- Inline TVF → `RETURNS TABLE (cols) LANGUAGE sql AS $$ <select> $$` (column types from the
  semantic model; unknown type → `S2P5010`, Error).
- Multi-statement TVF (`RETURNS @t TABLE (…)`) → `RETURNS TABLE (…)` plpgsql; `@t` becomes a
  temp table `_ret_<name>` created at the start (`ON COMMIT DROP`, dropped first if exists);
  `INSERT @t …` targets it; `RETURN` → `RETURN QUERY SELECT * FROM _ret_<name>; RETURN;`.
- `CROSS APPLY dbo.tvf(x)` resolves to the converted function.

#### WP 3.4 — Procedures (L)

- `CREATE PROCEDURE p @a int, @b int OUTPUT` → `CREATE PROCEDURE p(a integer, INOUT b integer
  DEFAULT NULL)`; T-SQL defaults → `DEFAULT`; after the first defaulted parameter every later
  one needs a default — if SQL Server's order breaks that, add `DEFAULT NULL` and `S2P5020`
  (Info).
- Result sets: count the statements that return rows to the caller (a `SELECT` not assigning
  variables and not feeding `INSERT`). None → `PROCEDURE`. One, at the end, and `ResultSetStyle
  = Function`, and no OUTPUT parameters → `FUNCTION … RETURNS TABLE (…)` with `RETURN QUERY`.
  If a procedure has both a result set and OUTPUT parameters, or more than one result set
  (or `ResultSetStyle = Refcursor`), use `PROCEDURE` with one `INOUT <name>_rs refcursor` per result set (`OPEN
  … FOR <select>`), and `S2P5021` (Warning: callers change).
- `RETURN <int>` in a procedure → extra `INOUT return_code integer DEFAULT NULL`, assigned
  before `RETURN` (`S2P5022`, Info).
- `EXEC p @a = 1, @b = @x OUTPUT` → `CALL p(a => 1, b => v_x)` (INOUT values come back into
  the variable); a procedure converted to a function → `PERFORM`/`SELECT * FROM`/`INSERT …
  SELECT * FROM f(…)` (this resolves `INSERT … EXEC`). Calls to system procedures
  (`sp_*`, `xp_*`) → `S2P5023` (Error) except `sp_executesql` (WP 3.8).
- Transactions: `BEGIN TRAN` → dropped (a procedure runs inside the caller's transaction);
  `COMMIT` → `COMMIT`; `ROLLBACK` → `ROLLBACK`; `SAVE TRAN`/`ROLLBACK TRAN name` → `S2P5024`
  (Error); `@@TRANCOUNT` → `S2P5025` (Error). A procedure that commits cannot be converted to a
  function; force `PROCEDURE`.

#### WP 3.5 — Temp tables and table variables (M)

- `CREATE TABLE #t (…)` → `CREATE TEMP TABLE t (…) ON COMMIT PRESERVE ROWS` preceded by `DROP
  TABLE IF EXISTS t;` (SQL Server drops a procedure's temp tables when it ends; emit `DROP
  TABLE IF EXISTS t;` before each `RETURN` and at the end too). Name collisions with real tables
  → prefix `tmp_`.
- `DECLARE @t TABLE (…)` → same as a temp table named `tv_<name>`.
- `##global` temp tables → `S2P5030` (Error).
- `OUTPUT … INTO @t` → `WITH x AS (<dml> RETURNING …) INSERT INTO tv_t SELECT * FROM x`.

#### WP 3.6 — Error handling (M)

- `BEGIN TRY … END TRY BEGIN CATCH … END CATCH` → `BEGIN <try> EXCEPTION WHEN OTHERS THEN
  <catch> END;`, with `GET STACKED DIAGNOSTICS` at the start of the handler filling
  `v_error_message` (`MESSAGE_TEXT`), `v_error_state`, `v_error_number` (`RETURNED_SQLSTATE`
  mapped through a small table, else `50000`) only for the `ERROR_*()` functions actually used.
- `ERROR_MESSAGE()` → `v_error_message`, `ERROR_NUMBER()` → `v_error_number`,
  `ERROR_LINE()` → `NULL` with `S2P5040` (Info), `ERROR_PROCEDURE()` → the routine name literal,
  `ERROR_SEVERITY()` → `16`, `XACT_STATE()` → `-1` inside a handler (`S2P5041`, Info).
- A `COMMIT`/`ROLLBACK` inside a block with an exception handler is invalid in PL/pgSQL →
  `S2P5042` (Error).

#### WP 3.7 — Cursors (M)

- The common pattern `DECLARE c CURSOR FOR <select>; OPEN c; FETCH NEXT FROM c INTO @a, @b;
  WHILE @@FETCH_STATUS = 0 BEGIN … FETCH NEXT … END; CLOSE c; DEALLOCATE c;` → `FOR r IN
  <select> LOOP … END LOOP;` with `v_a := r.a` assignments (or direct `r.a` references).
- Other cursor shapes → explicit `DECLARE c CURSOR FOR …; OPEN c; FETCH c INTO …; EXIT WHEN NOT
  FOUND;`. `SCROLL`, `FETCH PRIOR/ABSOLUTE`, `WHERE CURRENT OF` → supported with the
  corresponding PL/pgSQL forms; `GLOBAL` cursors → `S2P5050` (Error).

#### WP 3.8 — Dynamic SQL (M)

- `EXEC('<literal>')` / `sp_executesql N'<literal>', N'@p int', @p = @x` → translate the
  literal as T-SQL and emit `EXECUTE '<translated>' USING v_x` (with `$1…` placeholders).
- Concatenated SQL built from variables: when every concatenated piece is a literal or a
  variable used as an *identifier* (`QUOTENAME(@t)`), emit `EXECUTE format('… %I …', v_t)`;
  values concatenated in → `%L`. Anything else → `S2P5060` (Error) with the original kept.

#### WP 3.9 — Triggers (L)

- `CREATE TRIGGER tr ON t AFTER INSERT, UPDATE` → a function `tr_fn() RETURNS trigger LANGUAGE
  plpgsql` plus one `CREATE TRIGGER` per event (`AFTER INSERT … REFERENCING NEW TABLE AS
  inserted FOR EACH STATEMENT`; UPDATE has both `NEW TABLE AS inserted OLD TABLE AS
  deleted`; DELETE has `OLD TABLE AS deleted`). The body references `inserted`/`deleted` as
  tables, so most T-SQL trigger bodies translate as queries over them unchanged. Return `NULL`.
- `UPDATE(col)` → `S2P6001` (Warning) translated as `true` unless the trigger is `FOR EACH ROW`
  compatible; `COLUMNS_UPDATED()` → `S2P6002` (Error).
- `INSTEAD OF` on a view → `INSTEAD OF … FOR EACH ROW` trigger; rewrite set-based bodies over
  `inserted` to use `NEW` (single-row) — only when the body is a single DML statement over
  `inserted`; else `S2P6003` (Error).
- `INSTEAD OF` on a table → `S2P6004` (Error). DDL and logon triggers → `S2P6005` (Error).
- `ROLLBACK` inside a trigger → `RAISE EXCEPTION 'transaction rolled back by trigger %', TG_NAME`.
- Trigger order (`sp_settriggerorder`) → PostgreSQL fires alphabetically; prefix names to keep
  the order and note it (`S2P6006`, Info).

#### WP 3.10 — Assessment v2 and documentation (M)

- `assess` covers modules: per object status, diagnostic codes, and an effort estimate (sum of
  per-code weights defined in `DiagnosticCodes`: Info 0, Warning 1, Error 5 by default).
- `--html report.html`: a single self-contained HTML file (no external scripts) with the
  summary and a sortable per-object table.
- `docs/diagnostics.md` complete for every code that exists.
- Corpus: compile scan covers all module kinds; result agreement adds procedure calls.

**Done when:** every corpus routine and trigger is `Converted`/`ConvertedWithWarnings` and
applies, or `Manual` with a specific code; no `S2P9001` anywhere in the corpus. **This completes
v0.3.**

---

### Stage A — application code (C# and VB.NET, with Roslyn)

**Goal:** the application side of a migration. `sql2pg code <path>` reads C# and VB.NET source
(a file, a folder, a `.csproj`/`.vbproj` or a `.sln`/`.slnx`) and writes converted copies, so
code that used SQL Server through ADO.NET, Dapper, Dapper.Contrib or Entity Framework Core uses
PostgreSQL through Npgsql. It changes what must change and reports the rest with `S2P8xxx`
codes. It never edits in place unless `--in-place` is given.

Rules for the whole stage:

- **Clean room**, as for the rest of the repository. The sources of truth are the provider APIs
  as documented by their owners (Npgsql, EF Core and Dapper), and tests that compile and run the
  converted code.
- **Roslyn as a library.** Use `Microsoft.CodeAnalysis.CSharp` and `.VisualBasic`, syntax trees
  and semantic models. When the project's references are not available, recognise names
  syntactically (`SqlConnection` with a `Microsoft.Data.SqlClient` or `System.Data.SqlClient`
  import).
- **Language-neutral edits.** A finder produces a list of edits (span, replacement text, reason).
  The engine applies them to the source text, so formatting and comments survive. Each edit is
  written in the file's language, for example string literals in C# or VB form.
- **Reuse the SQL translator.** SQL in code goes through `ScriptTranslator` in a new
  *parameters* mode. In that mode an undeclared `@name` is a command parameter and stays
  `@name`, which Npgsql binds by name. It does not start a DO block.
- **Optional schema.** With `--source` (the same as `sql2pg schema`), the converter knows tables,
  identity columns and each procedure's PostgreSQL shape (function, procedure, refcursors).
  Without it, those rewrites are reported instead of made.

#### WP A.1 — Project, edit engine and CLI (M)

- New project `Majorsilence.Sql2Postgre.CodeConversion` (library) and its unit tests, plus the
  verb `sql2pg code <path> --out <dir> [--in-place] [--source …] [--config …] [--json …]`.
- Input discovery: `.cs`/`.vb` files under a folder (skipping `bin/` and `obj/`); the files a
  `.csproj`/`.vbproj` compiles, by default glob; the projects of a `.sln`/`.slnx`.
- `CodeEdit(Span, Replacement, Code, Message)`, a per-file `CodeConversionResult` (new text,
  edits, diagnostics), and an engine that applies non-overlapping edits from the end. Two edits
  that overlap are a bug: S2P9001.
- `ICodeLanguage` with C# and VB implementations: parse, string literal reading (regular,
  verbatim, raw, interpolated, VB) and writing, and how to write `using`/`Imports`.
- Output mirrors the input tree. A report lists each file's edits and diagnostics, and the CLI
  prints a summary and exits 2 when anything needs a person.
- Tests: the engine on overlapping, adjacent and empty edits; round trip of untouched files
  byte for byte; literal reading and writing for each form in both languages.

#### WP A.2 — ADO.NET (M)

- Namespaces: `Microsoft.Data.SqlClient` / `System.Data.SqlClient` → `Npgsql` (and
  `NpgsqlTypes` when `NpgsqlDbType` is used).
- Types: `SqlConnection`, `SqlCommand`, `SqlDataReader`, `SqlParameter`, `SqlTransaction`,
  `SqlDataAdapter`, `SqlCommandBuilder`, `SqlConnectionStringBuilder`, `SqlParameterCollection`
  → their `Npgsql…` counterparts. `SqlException` → `PostgresException` (S2P8001, Warning).
  `.Number` values in the code are mapped to SQLSTATEs where the error is the same: 2627/2601 →
  `23505`, 547 → `23503`/`23514`, 515 → `23502`, 1205 → `40P01`. Anything else is S2P8002
  (Error).
- `SqlDbType.X` → `NpgsqlDbType.Y` (and `.SqlDbType =` → `.NpgsqlDbType =`), following the
  type mapper. `Structured` (table-valued parameters) → S2P8003 (Error).
- `SqlBulkCopy` → S2P8004 (Error): the replacement is Npgsql's binary COPY, whose shape
  differs.
- Connection strings in code (literals passed to a connection, or assigned to
  `ConnectionString`):
  - keys are rewritten: `Server`/`Data Source` → `Host` (with `,port` → `Port`),
    `Initial Catalog`/`Database` → `Database`, `User Id` → `Username`, `Password` kept;
  - `Integrated Security` and `Trusted_Connection` → S2P8005 (Warning);
  - SQL Server-only keys are dropped with S2P8006 (Info).
- `SqlTypes` (`SqlString`, `GetSqlInt32` …) → S2P8007 (Error).
- Tests: golden C# and VB files per rule. Both-engines tests compile a sample program
  (in memory, with Roslyn) before and after conversion, run it against the SQL Server and
  PostgreSQL containers, and compare its output.

#### WP A.3 — SQL in code (L)

- SQL sites:
  - `new SqlCommand(sql, …)`, `.CommandText = sql`;
  - Dapper's `Query*`, `Execute*` and `QueryMultiple*` (first argument, or `sql:`);
  - EF Core's `FromSqlRaw`, `ExecuteSqlRaw`, `SqlQueryRaw` (`*Async` too), and the
    `FormattableString` forms (`FromSql`, `FromSqlInterpolated`, `ExecuteSql…`).
- Following the text back: a literal, a `const`, a `static readonly` field, or a local assigned
  once from one of those. Concatenations of those are joined. Any other piece is S2P8010 (Error,
  "SQL built at run time"), and the site is left alone.
- Translation goes through `ScriptTranslator` in parameters mode; diagnostics carry over, mapped
  to the file and line. `{0}` placeholders in EF `…Raw` strings and interpolation holes in
  `FormattableString` forms are markers that come back unchanged.
- App-specific rules (in the translator, parameters mode only):
  - `INSERT …; SELECT SCOPE_IDENTITY()` (or `@@IDENTITY`, `CAST(… AS int)`) → `INSERT …
    RETURNING <identity column>`, which needs `--source` (S2P8011 without it);
  - `SELECT COUNT(*)` read with `ExecuteScalar` and cast to `int` → `count(*)::integer`
    (PostgreSQL's count is bigint, and the cast in C# would throw). The column of every
    translated `COUNT` is checked.
- Tests: golden C#/VB for each site and each source of text; both-engines run of a sample
  program with parameters, identity retrieval and counts.

#### WP A.4 — Stored procedure calls (M)

- `CommandType.StoredProcedure` (ADO.NET, and Dapper's `commandType:`), with `--source`:
  - a procedure converted to a function → `CommandType.Text` with `SELECT * FROM f(@a, @b)`;
  - a procedure → stays StoredProcedure (Npgsql issues `CALL`). Output parameters become
    `InputOutput` (PostgreSQL's INOUT). A `ReturnValue` parameter becomes the INOUT
    `return_code`;
  - a procedure returning refcursors → S2P8020 (Error): the caller must fetch the cursors in a
    transaction.
- Without `--source`: S2P8021 (Warning), "which form depends on the converted schema".
- Parameter names keep their T-SQL names without `@`. Npgsql matches them to the routine's
  parameters by name.

#### WP A.5 — Dapper and Dapper.Contrib (S)

- Dapper calls are SQL sites (WP A.3) and procedure calls (WP A.4). Nothing else changes: Dapper
  maps columns to properties ignoring case, which suits lower-case names.
- Dapper.Contrib:
  - `[Table("…")]` with brackets or `dbo.` is rewritten to the converted name (`public.orders`);
  - `[Key]`/`[ExplicitKey]` keep working with Npgsql, which Dapper.Contrib supports;
  - `SqlMapperExtensions.GetDatabaseType` overrides and custom `ISqlAdapter`s → S2P8030 (Info).
- Tests: both-engines run of a sample using `Get`, `GetAll`, `Insert` (identity back),
  `Update` and `Delete`.

#### WP A.6 — Entity Framework Core (L)

- `UseSqlServer(…)` → `UseNpgsql(…)`; the options lambda's SQL Server-only calls
  (`EnableRetryOnFailure` is kept; `UseCompatibilityLevel` and the like are dropped with
  S2P8040, Info).
- Project files: `Microsoft.EntityFrameworkCore.SqlServer` →
  `Npgsql.EntityFrameworkCore.PostgreSQL` (the version matching the EF Core major), and
  `Microsoft.Data.SqlClient` → `Npgsql`.
- Model configuration:
  - `HasColumnType("…")` through the type mapper;
  - `HasDefaultValueSql("…")` and `HasComputedColumnSql("…")` through the expression
    translator;
  - `ToTable("T", "dbo")` and `[Table]`/`[Column]` names through the name mapper;
  - `UseIdentityColumn` → `UseIdentityByDefaultColumn`;
  - `IsClustered`, `IsMemoryOptimized` and the like are dropped with S2P8040.
- EF Core quotes names exactly as the model has them. With `NameStyle = Lower`, a model that does
  not name its tables and columns explicitly no longer matches the converted schema. The
  converter adds to `OnModelCreating` a loop that sets every table, column, key, index and
  foreign key name to the name mapper's form, and reports S2P8041 (Info).
- `EF.Functions` SQL Server methods: `DateDiffDay` and the like map to `EF.Functions` equivalents
  or plain date arithmetic where Npgsql translates it, else S2P8042 (Error).
- Migrations (`Migrations/*.cs` with `SqlServer:` annotations) → S2P8043 (Warning): regenerate
  them against PostgreSQL rather than convert them.
- Tests: golden files, plus a both-engines run of a small `DbContext` sample (create, query,
  update with the converted schema).

**Done when:** the sample programs (C# and VB) run on PostgreSQL with the same output as on SQL
Server after conversion; every rule has a golden test in both languages; every `S2P8xxx` code is
in `DiagnosticCodes` and `docs/diagnostics.md`.

### Stage 4 — the long tail

**Goal:** close the gaps the corpus and the code converter still report, largest first. The
order comes from the compile scan (`tests/corpus/results/*-pg18.json`, including the
`Untranslated` breakdown) and BACKLOG. At the end of Stage 3 and Stage A, the open items were:

- S2P5070 DDL in routines: 142, nearly all of them WideWorldImporters' and WideWorldImportersDW's
  load procedures.
- S2P3001 untranslatable expressions: 71. These are XML methods (7 AdventureWorks views, 2
  triggers), hierarchyid methods, full-text `CONTAINSTABLE`, and UDFs in computed columns.
- S2P5060 dynamic DDL: 48.
- WideWorldImporters' temporal tables, table-valued parameters, `OPENJSON … WITH` and geography
  columns.
- One agreement query at 50, caused by char padding.

Every WP follows the definition of done:
- golden tests for the translation rules;
- both-engines tests for the behaviour;
- codes documented;
- the corpus scan with 0 apply/copy/verify failures, and its Manual counts recorded in BACKLOG.

#### WP 4.1 — DDL inside routines (M)

The most frequent error left (S2P5070). Load procedures drop and re-create indexes, disable
constraints around bulk loads, and truncate.

- `CREATE [UNIQUE] [NON]CLUSTERED INDEX ix ON t (…) [INCLUDE (…)] [WHERE …]` on a real table →
  `CREATE INDEX` with the name, table and columns the schema converter gives
  (`SchemaTranslator`'s index translation, reused). `DROP INDEX ix ON t` /
  `DROP INDEX t.ix` → `DROP INDEX IF EXISTS schema.ix` (an index name is per schema in
  PostgreSQL).
- `ALTER INDEX ix ON t REBUILD|REORGANIZE` → `REINDEX INDEX schema.ix` (S2P5071, Info:
  REORGANIZE has no lighter form). `ALTER INDEX … DISABLE` / `REBUILD` used to re-enable →
  `DROP INDEX` / `CREATE INDEX` from the index's definition in the source model (S2P5072,
  Warning: the index is gone while disabled).
- `ALTER TABLE t NOCHECK CONSTRAINT fk` / `CHECK CONSTRAINT fk` (and `ALL`): PostgreSQL cannot
  switch a foreign key off. A NOCHECK…CHECK pair around a load in one routine → `ALTER TABLE …
  DROP CONSTRAINT` and `ADD CONSTRAINT … NOT VALID` then `VALIDATE CONSTRAINT`, from the
  source model's definition (S2P5073, Warning). An unpaired one is S2P5070 as now.
- `ALTER TABLE t ADD col …` / `DROP COLUMN` / `ALTER COLUMN` → the schema converter's column
  translation (S2P5074, Warning: the routine changes the schema at run time).
- Anything else stays S2P5070.
- Tests: golden files; a both-engines run of a load procedure that drops an index, disables a
  foreign key, loads and re-creates both.

#### WP 4.2 — System-versioned temporal tables (L)

WideWorldImporters keeps history for most of its tables.

- A table with `PERIOD FOR SYSTEM_TIME` and `SYSTEM_VERSIONING = ON (HISTORY_TABLE = h)`:
  - The current table keeps `ValidFrom`/`ValidTo` (timestamp). The history table is converted as
    a plain table.
  - A trigger (`BEFORE UPDATE OR DELETE … FOR EACH ROW`) copies the old row into the history
    table with `ValidTo = now()`, and sets the new row's `ValidFrom` (S2P2020, Info).
  - Defaults give `ValidFrom` the insert time and `ValidTo` `9999-12-31 23:59:59.9999999`, as SQL
    Server does.
- Queries:
  - `FOR SYSTEM_TIME AS OF @t` → `(SELECT … FROM t WHERE ValidFrom <= @t AND ValidTo > @t UNION ALL
    SELECT … FROM h WHERE …)`;
  - `FROM`, `BETWEEN`, `CONTAINED IN` and `ALL` likewise.
- `ALTER TABLE … SET (SYSTEM_VERSIONING = OFF|ON)` in routines: they are used around loads, so
  they become `ALTER TABLE … DISABLE|ENABLE TRIGGER` on the history trigger (S2P5075, Info).
- The data copy copies both tables with their period values as they are.
- Tests: golden files; a both-engines run of updates and deletes, then each `FOR SYSTEM_TIME`
  form compared row for row (the period values compared by order, since the clocks differ).

#### WP 4.3 — XML methods and XML indexes (M)

AdventureWorks' views over `Person.Demographics`, `Resume` and `Instructions`, and its triggers'
`.modify()`.

- `x.value('xpath', 'type')` → `CAST((xpath('xpath', x, nsarray))[1]::text AS type)`; text nodes
  and attributes as XPath 1.0 gives them. `WITH XMLNAMESPACES` and `declare namespace` in the
  query → the namespace array.
- `x.query('xpath')` → `xmlagg` over `xpath(…)`. `x.exist('xpath')` → `xpath_exists` (1/0).
- `CROSS APPLY x.nodes('xpath') AS T(c)` → `CROSS JOIN LATERAL unnest(xpath('xpath', x, ns)) AS
  T(c)`; `c.value(…)` over it as above.
- `x.modify('insert|replace value of|delete …')` → S2P3040 (Error): PostgreSQL's xml type has no
  update functions. It names the statement to rewrite: build the document anew in the
  application or with `xmlelement`.
- XQuery beyond XPath 1.0 (FLWOR, `sql:variable`, `sql:column`) → S2P3041 (Error).
- XML indexes (primary and secondary) → an expression index on the XPath values the converted
  views and routines read, when there are any; otherwise a note (S2P2003 as now).
- Tests: golden files; both-engines result agreement on the AdventureWorks XML views, which
  become corpus queries.

#### WP 4.4 — hierarchyid (M)

hierarchyid is converted to its text form (`/1/2/`, S2P1105). Its methods become
`mssql_compat` helpers over that text, which are plain SQL functions with no extension:

- `GetLevel()`, `GetAncestor(n)`, `IsDescendantOf(p)`, `GetRoot()`, `ToString()`,
  `Parse(s)`, `GetDescendant(a, b)` and `GetReparentedValue(old, new)` → the corresponding helpers.
- Ordering by a hierarchyid column → a sort key helper (`mssql_compat.hierarchyid_key(x)`, an
  integer array), so ORDER BY and indexes sort depth-first as SQL Server does.
- Computed columns using `GetLevel()` (AdventureWorks' `OrganizationLevel`) become stored
  generated columns; the helper is IMMUTABLE.
- Tests: helper unit tests on both engines over a grid of paths; AdventureWorks'
  `uspGetEmployeeManagers` and `uspGetManagerEmployees` converted and in result agreement.

#### WP 4.5 — Table-valued parameters (M)

`Website.OrderList` and `OrderLineList` in WideWorldImporters, and `SqlDbType.Structured` in
application code (S2P8003).

- A user-defined table type → a composite type (as now, S2P2005) used as an array: a
  `READONLY` parameter `@t TableType` becomes `t tabletype[]`, and reads of `@t` become
  `unnest(routine.t)`.
- Procedures and functions taking one become callable from SQL with
  `ARRAY[ROW(…)::type, …]`, and from Npgsql with an array of a mapped composite.
- Application code (`sql2pg code`): a `DataTable` or `IEnumerable<SqlDataRecord>` passed as
  `Structured`. The converter adds a record class with the type's columns. It also adds a
  `MapComposite<T>` registration on the data source, and passes the rows as `T[]` (S2P8050,
  Warning: the code that fills the rows changes too).
- Tests: golden files; a both-engines run of `Website.InsertCustomerOrders`-style calls from SQL
  and from a C# sample.

#### WP 4.6 — JSON: OPENJSON and FOR JSON (M)

- `OPENJSON(@j)` with no WITH → `jsonb_each` / `jsonb_array_elements` (key, value, type
  columns as SQL Server's).
- `OPENJSON(@j, '$.path') WITH (c type '$.p' [AS JSON])` → `jsonb_to_recordset` where every path
  is a plain member, else `jsonb_array_elements` with `->>`/`#>>` per column and a cast. This
  includes WideWorldImporters' `Website.RecordVehicleTemperature`.
- `FOR JSON AUTO` over joins → nested `json_agg` per joined table, in SQL Server's nesting order.
  `FOR JSON PATH` with dotted names over joins likewise.
- `JSON_MODIFY` → `jsonb_set` / `jsonb_insert` / `#-` by its mode; `ISJSON(x, type)`.
- Tests: golden files; both-engines result agreement on JSON shapes, compared as parsed JSON.

#### WP 4.7 — Full-text search (M)

AdventureWorks' full-text indexes and `uspSearchCandidateResumes`.

- A full-text index on (columns) → a GIN expression index on
  `to_tsvector(config, coalesce(c1,'') || ' ' || …)`. The config comes from the index's language:
  `english` for 1033, otherwise reported (S2P2021).
- `CONTAINS(cols, 'term')`, `CONTAINS(cols, '"a" AND "b*"')`, `FREETEXT(cols, text)` → the same
  expression `@@ to_tsquery` / `websearch_to_tsquery`, mapping AND/OR/NOT, prefix `*`, phrase
  and `NEAR` (→ `<N>`).
- `CONTAINSTABLE`/`FREETEXTTABLE` → a lateral query with `ts_rank` as `RANK` and the key as
  `KEY`, for `JOIN … ON KEY_TBL.[KEY] = t.id`.
- Ranking values and stemming differ from SQL Server's (S2P3042, Info). Thesaurus and stoplists
  are reported.
- Tests: golden files; a both-engines check that the same rows match. Ranks are compared by
  order only.

#### WP 4.8 — Spatial methods with PostGIS (M, `postgis` opt-in)

With `postgis` enabled, geography/geometry columns are PostGIS types (WP 1.5). The methods then
map to `ST_*`:

- `STDistance`, `STIntersects`, `STContains`, `STWithin`, `STBuffer`, `STArea`, `STLength`,
  `STAsText`, `STAsBinary`, `STX`/`Lat`, `STY`/`Long` and `STSrid`;
- `geography::Point(lat, long, srid)`, `STGeomFromText`, `STPointFromText`, `Parse`;
- geography distances are in metres on both engines.

`MakeValid`, `Reduce` and `STUnion` map where an `ST_` function does the same; others are
S2P3030 as now. Spatial indexes become GiST (WP 1.8 notes them today). Without `postgis`,
everything stays as now (WKB bytea, S2P1106/S2P3030).

Tests: a both-engines grid of each method over points, lines and polygons. Results are compared
to 1e-6 relative, and SQL Server's and PostGIS's geodesic models are recorded where they differ.

#### WP 4.9 — Character semantics (S)

- A `char(n)` value that a CASE, `+`, ISNULL/COALESCE or a varchar CAST turns into varchar keeps
  its trailing spaces in SQL Server. PostgreSQL's bpchar-to-text drops them. Such an operand is
  wrapped in `rpad(x::text, n)` when the result is a string. This is the pubs agreement query at
  50.
- The semantic model types a CASE (and COALESCE/ISNULL/IIF) by SQL Server's precedence and the
  longest length, not the first branch's.
- LIKE on char columns: trailing spaces as SQL Server treats them.
- Tests: the expression grid on both engines with char values; `pubs/32-proc-reptq3` reaches
  100.

#### WP 4.10 — Catalog queries in code (M)

Code that asks SQL Server about itself.

- `OBJECT_ID('schema.name'[, type])` → `to_regclass` / `to_regproc` on the converted name, as an
  integer oid (or NULL), as WP 3.5 already does for temp tables.
- `COL_LENGTH`, `COLUMNPROPERTY(…, 'IsIdentity')`, `OBJECTPROPERTY(…, 'IsUserTable')`,
  `DB_ID`, `SCHEMA_ID`, `SCHEMA_NAME`, `OBJECT_NAME`, `TYPE_NAME` → catalog lookups.
- `INFORMATION_SCHEMA.TABLES|COLUMNS|ROUTINES|…` → PostgreSQL's `information_schema`, which
  has the same views. Names are compared through the name mapper: SQL Server's
  `TABLE_SCHEMA = 'dbo'` → `'public'`.
- `sys.tables`, `sys.columns`, `sys.objects`, `sys.indexes`, `sys.schemas`, `sys.procedures` in
  the common join shapes → `pg_catalog` equivalents (`pg_class`, `pg_attribute`, `pg_namespace`,
  `pg_proc`, `pg_index`), with only the columns that have a counterpart. Others are S2P3043
  (Error).
- `sp_rename`, `sp_help`, `sp_columns`, `sp_tables` in scripts → `ALTER … RENAME` / catalog
  queries (S2P5023 today).
- Tests: golden files; both-engines result agreement of catalog queries over the shop fixture
  (names compared through the name mapper).

#### WP 4.11 — Connection strings in configuration files (S)

`sql2pg code` also converts:

- `appsettings*.json`: the `ConnectionStrings` section, and any string value that is a SQL
  Server connection string;
- `web.config` / `app.config`: `<connectionStrings><add connectionString="…"
  providerName="System.Data.SqlClient"/>` gets an Npgsql connection string and
  `providerName="Npgsql"`;
- `*.dbproj`/`*.sqlproj` are left alone (S2P8051, Info).

They use the same connection-string conversion as WP A.2, and the files' formatting is kept
(text edits). Secrets are never printed: diagnostics name the key, not the value. Tests: golden
files for JSON (comments allowed) and XML.

#### WP 4.12 — Error context through calls (S)

`ERROR_*()` in a procedure called from a caller's CATCH block is NULL today (S2P5043).
AdventureWorks' `uspLogError` and `uspPrintError` work this way.

- Each converted handler stores the error with
  `set_config('sql2pg.error_number' | 'error_message' | …, value, true)` (transaction-local).
  `ERROR_*()` outside a CATCH block reads `current_setting('sql2pg.error_…', true)`.
- Leaving the handler clears the settings, so `ERROR_*()` is NULL afterwards, as in SQL Server.
- Tests: a both-engines run of a procedure whose CATCH block calls a logging procedure; the log
  rows agree.

#### WP 4.13 — Remaining DML forms (M)

These come from BACKLOG:

- UPDATE/DELETE whose target is inside an outer join → a key-based rewrite
  (`WHERE key IN (SELECT … FROM … LEFT JOIN …)`);
- `UPDATE TOP (n)` / `DELETE TOP (n)` → a key-based rewrite with `LIMIT n` (an arbitrary n rows,
  as in SQL Server; S2P4009 becomes Info);
- `FOR XML RAW|AUTO|PATH` beyond the `PATH('')` concatenation → `xmlelement`/`xmlagg`;
- `PIVOT`/`UNPIVOT` forms the translator does not take yet.

Tests: golden files and both-engines agreement for each.

#### WP 4.14 — Trigram indexes (S, `pg_trgm` opt-in)

With `pg_trgm` enabled, a column that converted views and routines search with a
leading-wildcard `LIKE '%…%'` (or that a `--trigram-index schema.table.column` option names) gets
`CREATE INDEX … USING gin (col gin_trgm_ops)`. This is reported (S2P2022, Info), so the space
cost is a choice. Tests: the index is created and used (`EXPLAIN`) on PostgreSQL 18.

#### WP 4.15 — SQL Server Agent jobs (M, `pg_cron` opt-in)

- `sql2pg schema` reads `msdb` jobs whose steps are T-SQL, along with their schedules
  (`sysjobs`, `sysjobsteps`, `sysschedules`) when the source is a live database and
  `--agent-jobs` is given.
- Each job becomes a converted procedure (the steps in order, with their on-failure actions as
  far as one procedure can express them), plus `cron.schedule(name, cron expression, 'CALL …')`.
- Daily, weekly and monthly schedules map to cron expressions. Others (on idle, on start, every N
  seconds) are reported (S2P2023).
- Other step types (SSIS, PowerShell, CmdExec) are reported (S2P2024).
- Tests: the schedule mapping as unit tests. A container test runs on PostgreSQL 18 with
  pg_cron: the CI image gets it, or the test is skipped where pg_cron is not installed.

#### WP 4.16 — Incremental data sync (L)

`sql2pg data --since <state>` copies rows changed since the last run, for a cut-over with a
short outage:

- With a `rowversion` column, rows whose rowversion is above the last value copied.
- With SQL Server change tracking enabled, `CHANGETABLE(CHANGES …)` gives the changes, deletes
  included.
- Otherwise the table is copied whole again (the plan says which applies to each table).
- Changed rows are upserted (`INSERT … ON CONFLICT (pk) DO UPDATE`), and deletes are applied when
  change tracking reports them. Sequences and identities are moved past the new values.
- The state file records each table's high-water mark. `--verify` checks counts and checksums
  after the last pass.
- Tests: a container test runs a full copy, changes rows on SQL Server, runs an incremental copy,
  and verifies.

#### WP 4.17 — PostgreSQL 15, 16 and 17 (M)

The maintainer chose this on 2026-11-19. PostgreSQL 18 stays the default target; `TargetVersion`
15–17 gives output that runs on those versions:

- Generated columns: VIRTUAL on 18, STORED before (S2P2001), as since Stage 1.
- `NEWSEQUENTIALID()`: `uuidv7()` on 18, `gen_random_uuid()` before (S2P3002), as since Stage 1.
- OUTPUT: 18 writes `inserted`/`deleted` as `RETURNING`'s `new`/`old`. Before 18, `RETURNING`
  sees the target row only: `INSERT`/`DELETE` and an `UPDATE`'s `inserted` read the target's
  columns, an `UPDATE`'s `deleted` reads a copy of the table joined by primary key (ctid without
  one), and `MERGE … OUTPUT` needs 17 (S2P4015).
- `MERGE … WHEN NOT MATCHED BY SOURCE` (new in 17): before 17, each by-source action is a
  data-modifying CTE on the rows no source row matches (S2P4016).
- LIKE on the case-insensitive ICU collation (`IcuCollation`): before 18 it becomes
  `lower(x COLLATE "default") LIKE lower(p)`; SIMILAR TO does on every version (S2P1302).
- Tests: container tests take `TargetVersion` from `SQL2PG_PG_VERSION`; CI runs them on 15, 16, 17
  and 18; golden cases at 15 and 17; the corpus scan is clean on all four.

#### WP 4.18 — DDL in dynamic SQL (M)

Configuration and load procedures build DDL as strings and run it: WideWorldImporters and
WideWorldImportersDW had 125 S2P5060 and 17 S2P5070 from it, which kept 24 procedures Manual.

- **Constants in the text.** A piece of the dynamic SQL is folded into its text when it is a
  constant where the text is put together: a variable last set to a literal, `NCHAR(13)`,
  `QUOTENAME(N'x')`, `CAST` of a number, a `CASE` on constants. A variable assigned anywhere in a
  `WHILE` around the EXEC is not a constant (this also fixes following the SQL text itself).
- **Created objects.** `CREATE PROCEDURE` / `FUNCTION` / `TRIGGER` in dynamic SQL of known text
  is translated as the database's own would be, and `EXECUTE` runs the PostgreSQL DDL. The
  semantic model registers these objects (without converting them on their own), so procedures
  that call them resolve and DROP TRIGGER finds their tables. Two different definitions of one
  name are reported.
- **DDL a routine runs**, in its body or in dynamic SQL: WP 4.1's forms, plus `DROP TRIGGER`
  (trigger and trigger function, per event), `ALTER SEQUENCE … RESTART`, and security policies as
  row-level security (S2P5076).
- Each translated statement runs as an `EXECUTE` of its own; a number put in at run time where
  T-SQL takes only a number parses through a numeric marker.
- Found on the way and translated: `SESSION_CONTEXT` (S2P3045), `STRING_ESCAPE(x, 'json')`,
  `CONVERT` styles 6, 7, 106 and 107.
- Not covered: dynamic `CREATE TABLE`/`TYPE`, `ALTER DATABASE`, in-memory OLTP, and security
  policies in the source catalog (only those procedures create).
- Tests: golden files for the DDL and the functions; a both-engines run of procedures that
  create and drop a trigger, create and call a procedure, apply and remove a security policy
  (read as an ordinary role) and restart a sequence, each run twice; the function grid for the
  new functions; corpus scans on 15–18.

#### WP 4.19 — Roles, catalog views and server feature checks (M)

After WP 4.18, catalog views the converter did not answer (S2P3043) were the largest group of
errors left in the corpus: role views kept two WideWorldImporters procedures Manual on their own,
and `sys.indexes`, audits, partitioning and filegroups came up in the configuration procedures.

- **Roles.** `sys.database_principals` and `sys.database_role_members` as mssql_compat views over
  `pg_roles` and `pg_auth_members`. `CREATE ROLE`, `ALTER ROLE … ADD MEMBER` / `DROP MEMBER` /
  `WITH NAME =` and `DROP ROLE` in routines and their dynamic SQL as PostgreSQL roles (S2P5077).
  Role names are kept as written (no name style, also when dynamic SQL puts them in), and
  reported as belonging to the server rather than the database.
- **`sys.indexes`** over a new `mssql_compat.index_names` table the converter fills, as it fills
  `object_names`: each table's primary key, unique constraints and indexes under their SQL Server
  names, with SQL Server's `type` (heap rows included), listed while the PostgreSQL index exists.
- **Server feature checks.** An IF on `SERVERPROPERTY` values PostgreSQL answers with a constant
  (`IsXTPSupported` 0, `IsFullTextInstalled` 1, `EngineEdition` 3, …) is decided at conversion:
  the branch that never runs stays as a comment (S2P3046), so procedures that set up In-Memory
  OLTP, columnstore, partitioning or PolyBase behind such a check convert, and behave as on a
  SQL Server edition without the feature.
- **Specific messages.** The catalog views of features PostgreSQL does not have (audits,
  partition functions and schemes, files and filegroups, full-text catalogs, In-Memory OLTP,
  logins, configuration, languages, dynamic management views) say which feature, under S2P3043.
- Not covered: audits (no core equivalent), `sys.partitions`, users (`CREATE USER`), fixed
  database roles, `sp_addrolemember`, and `ALTER`/`CREATE` of a procedure the database already
  has with another definition (the full-text versions of WideWorldImporters' search procedures).
- Tests: golden files for the roles, feature checks and catalog queries; a both-engines run of
  procedures that create a role, add and remove a member and drop it (role names new per run),
  of `sys.indexes` for keys, a filtered index and a heap, and of feature checks; corpus scans on
  15–18.

#### WP 4.20 — PostgreSQL 19 (S)

PostgreSQL 19 was released on 2026-11-19 (the image was on Docker Hub the next day), and the
maintainer asked for it on 2026-11-20. 18 stays the default target.

- `TargetVersion` 19, in the options, `--target-version` and CI (`pg: [15, 16, 17, 18, 19]`).
- **Keywords.** 19's new keywords (`ignore`, `lsn`, `repack`, `respect`, `wait`) are all
  unreserved, so names are quoted as for 18 (checked with `pg_get_keywords()` on each version).
- **Window functions with `IGNORE NULLS`.** 19 adds `IGNORE NULLS` to `first_value`,
  `last_value`, `lag`, `lead` and `nth_value`; SQL Server 2022 has it on the first four. Written
  as it is for 19; before 19 it is reported (S2P3047). The converter used to drop it without a
  diagnostic, on every version. With an offset other than 1, or a default, SQL Server's `LAG`/`LEAD`
  do not count non-NULL rows as the standard (and PostgreSQL) does, so those are written as a
  frame from the row n away.
- The rest of the 19 release notes was checked against what the output relies on: no change to
  the catalogs mssql_compat reads, to MERGE or RETURNING, or to `to_char`.
  `standard_conforming_strings` can no longer be off, which the output never needs;
  `json_array()` over no rows (not used) now gives `[]`.
- **Included columns.** 19 compresses with lz4 by default (`default_toast_compression`), which
  left AdventureWorks' `IX_ProductReview_ProductID_Name` (`INCLUDE (Comments)`, an
  `nvarchar(3850)`) with rows over btree's 2704 bytes. SQL Server does not limit included columns,
  so an incompressible comment failed the INSERT on every version. Included columns that could
  make the row too long are now left out, widest first (S2P2026).
- **Images.** postgis/postgis has no 19 image yet, so the container tests build one from PGDG's
  `postgresql-19-postgis-3` (as they build the pg_cron image).
- Tests: golden cases at 18 and 19; a both-engines test of `IGNORE NULLS`; the container tests
  and the corpus scan on 15–19.

#### WP 4.21 — Syntax the translator reads past (M)

WP 4.20 found `IGNORE NULLS` dropped without a diagnostic: the translator never read
`FunctionCall.IgnoreRespectNulls`. A reflection inventory of the ScriptDom node types the
translator names, against the property names its source uses, found more of the same, and a
probe of each that could change results confirmed these:

- **Named windows.** `WINDOW w AS (…)` was dropped and `OVER w` became `OVER ()`. Written as
  PostgreSQL's `WINDOW` clause and `OVER w` / `OVER (w ORDER BY …)`.
- **`TRIM(LEADING | TRAILING | BOTH chars FROM s)`** became `btrim` whatever the option. Now
  `ltrim`, `rtrim` or `btrim`.
- **`TABLESAMPLE`** was dropped, so the query read the whole table. `TABLESAMPLE (n PERCENT)
  [REPEATABLE (seed)]` becomes `TABLESAMPLE SYSTEM (n) [REPEATABLE (seed)]`, which samples pages
  as SQL Server does; `n ROWS` is reported (S2P4017).
- **`ALTER TABLE … ADD c … NULL DEFAULT x`** without `WITH VALUES` in a routine leaves the existing
  rows NULL in SQL Server; PostgreSQL's `ADD COLUMN … DEFAULT x` fills them. Now the column is
  added, then its default set.
- **`WITH XMLNAMESPACES`** was dropped (the prefixes stayed, undeclared): reported (S2P3048).
- **`EXEC ('…') AS USER = 'name'`** ran as the caller without a word: reported (S2P5063).
- **HIDDEN, masked and Always Encrypted columns** were neither read nor reported: `SELECT *`
  leaves a hidden column out in SQL Server, masking hides values from readers without `UNMASK`,
  and an encrypted column's values are ciphertext the server cannot read. The readers now record
  them and the column is reported (S2P2027).
- **Session options that change results** (`SET DATEFIRST`, `DATEFORMAT`, `LANGUAGE`,
  `ROWCOUNT` in a script, `ANSI_NULLS OFF`, `CONCAT_NULL_YIELDS_NULL OFF`, `QUOTED_IDENTIFIER
  OFF`, `IMPLICIT_TRANSACTIONS ON`, …) were dropped with an Info (and in dynamic SQL without one);
  now a Warning that says what changes.
- **A guard.** A unit test repeats the inventory and fails when a node type the translator
  names has a property the source never reads and that is not on a reviewed list
  (`tests/…/Translation/ScriptDomUnreadProperties.txt`), each entry with the reason it cannot change results or is
  not SQL Server syntax. A ScriptDom upgrade that adds properties shows up there.
- Not covered: typed `xml` (an XML schema collection's validation), a temporal table's
  `HISTORY_RETENTION_PERIOD`; properties shared by name with one the translator reads are not
  found by the inventory.
- Tests: golden files for each translation and report; both-engines cases for named windows,
  `TRIM`, `TABLESAMPLE (100 PERCENT)` and `ADD … DEFAULT` with and without `WITH VALUES`; the
  catalog and script readers on HIDDEN and masked columns; corpus scans on 15–19.

#### WP 4.22 — The remaining small gaps (S)

Three small things WPs 4.17–4.21 left open, each with a known cause:

- **`TRY_CONVERT` with a style.** Only the style-less form was translated, so
  WideWorldImporters' `Sales.Invoices` (its computed `ConfirmedDeliveryTime` is
  `TRY_CONVERT(datetime2(7), JSON_VALUE(…), 126)`) and an index on it stayed Manual. Now: a
  date to text is `CONVERT`'s translation (it cannot fail); text to datetime2 or date under 126
  or 127 is a new IMMUTABLE helper, `mssql_compat.try_convert_iso8601`, which reads the ISO
  layouts SQL Server 2022 reads under those styles (established with a grid of inputs), so a
  computed column can use it (S2P3049); other styles stay reported (S2P3003).
- **Searches on the case-insensitive ICU collation** (`IcuCollation`). Before 18, `REPLACE` and
  `STRING_SPLIT` on such a column failed (PostgreSQL refuses substring searches on a
  nondeterministic collation); `CHARINDEX` failed before 18 and was case-sensitive on 18; and
  the helpers with regular expressions (`ISNUMERIC`) failed on every version. They are now
  case-insensitive matches on the default collation (S2P1302).
- **The two drops WP 4.21 listed as not covered**: a temporal table's `HISTORY_RETENTION_PERIOD`
  (S2P2028) and typed xml columns (S2P2027) are read from the catalog and scripts and reported.
- Tests: golden files; a both-engines grid of `TRY_CONVERT` 126/127 inputs; the case-insensitive
  searches on 15, 17 and 18; the readers on retention and typed xml; corpus scans on 15–19.

#### WP 4.23 — AdventureWorks' last Manual objects (M)

After WP 4.22 AdventureWorks had six Manual objects; two causes had a translation:

- **A user-defined function in a computed column or default** (`Sales.Customer.AccountNumber`
  calls `dbo.ufnLeadingZeros`). Functions were created after the tables, so such a column was
  left out, and `Sales.Customer` (and its key on the column) were Manual. A function a table
  definition calls is now created with the tables, before the table, when it is IMMUTABLE by
  the converter's volatility analysis (it reads no table and calls only immutable functions);
  so are the functions it calls. The column is STORED (PostgreSQL 18 allows no user-defined
  function in a VIRTUAL one).
- **INSTEAD OF triggers on tables** (`HumanResources.dEmployee`, `Purchasing.dVendor`; S2P6004,
  now a Warning). A BEFORE … FOR EACH ROW trigger that runs the body and returns NULL; while the
  body runs, a transaction-local setting lets the body's own statements on the table through,
  as SQL Server does not fire the trigger again for them. It runs per row, not for a statement
  that matches no rows, and the statement reports 0 rows.
- Found on the way, and fixed for every trigger: `ROLLBACK` in a trigger raised at once, so in
  TRY it ran the CATCH block (dVendor's would then end without an error); it now sets a flag,
  the rest of the trigger runs, and the trigger fails the statement when it returns, as SQL
  Server ends the batch (error 3609). `@@TRANCOUNT` in a trigger is 1 (0 after its ROLLBACK).
  And an IF or WHILE whose condition starts with a CASE (`IF CASE WHEN … THEN … END = 1`, as
  `IS_ROLEMEMBER` translates) is written in parentheses: PL/pgSQL reads a condition up to the
  first THEN, so it did not compile (the code phase sets `check_function_bodies = off`, so it
  failed only when it ran).
- Not covered: `xml.modify()` (S2P3040), in AdventureWorks' other two Manual triggers.
- Tests: golden files; a both-engines run of AdventureWorks' trigger patterns (a delete refused
  with ROLLBACK in TRY, a delete turned into an update, an insert rewritten) and of the
  computed column; corpus scans on 15–19.

#### WP 4.24 — xml.modify() (M)

AdventureWorks' last two Manual triggers (`Person.iuPerson`, `Sales.iduSalesOrderDetail`) are
Manual only for `xml.modify()` (S2P3040). PostgreSQL's xml type has no update functions, so the
document is rebuilt: `SET col.modify('…')` and `SET @x.modify('…')` become assignments of an
mssql_compat helper's result. The helpers find the target with `xpath` (the XPath the WP 4.3
path translation gives, so predicates and namespaces work as in `value()`), count the elements or
text nodes before it, and splice the document's text at that node.

- **`insert <constructor> (as first | as last)? into | before | after (path)[1]`**: one element
  constructor without enclosed expressions. Its elements take the prolog's default element
  namespace; the helper declares it on the inserted element when the namespace in scope there
  differs (SQL Server writes `xmlns=""` the same way).
- **`replace value of (path)[1] with expr`**, the target a text node, an attribute without a
  prefix, or (typed xml) an element of simple type. `expr` is a string, a number, `sql:column`,
  `sql:variable`, `data(path)` or a path, or `+ - * div` of them, typed as XQuery types them:
  untyped values become xs:double in arithmetic, typed ones take their type from the column's XML
  schema collection (which the readers now keep), and the result is written as SQL Server writes
  each type.
- As in SQL Server: a NULL document fails (error 5302), an empty new value fails (6325), and a
  target that is not found leaves the document as it is.
- Still reported (S2P3040): `delete`, `replace value of` without a single target, a constructor
  with enclosed expressions or more than one node, attributes with a prefix, and values of other
  SQL types (dates, binary). The inserted node keeps the text it was written with, where typed
  xml in SQL Server stores it normalised (`0.00` becomes `0`).
- Found on the way, and fixed for every trigger: `UPDATE(col)` of an xml (or json) column
  compared with `IS DISTINCT FROM`, which PostgreSQL has no operator for, so iuPerson failed on
  every UPDATE of Person; the text is compared now. And on 18 and 19 a computed column that is
  not persisted is VIRTUAL, which a trigger's transition tables and `NEW` give as NULL
  (iduSalesOrderDetail reads `inserted.LineTotal`); a computed column a trigger on its table
  names is STORED.
- Tests: a both-engines grid of documents for each form; golden files; AdventureWorks' two
  triggers in a both-engines run; corpus scans on 15–19.

#### Waiting for the maintainer

- `.dacpac` input, through the `Microsoft.SqlServer.DacFx` package
  ([microsoft/DacFx](https://github.com/microsoft/DacFx), as a dependency only, its license
  reviewed first). It is on hold; it becomes a WP when the maintainer asks.

## Definition of done

Every PR, in addition to its WP's own list:

- [ ] Builds with 0 warnings on net10.0 and net11.0.
- [ ] Unit tests pass on Windows and Linux; container tests pass in CI on PostgreSQL 15–19.
- [ ] New translation rules have golden tests; behavioural rules have both-engines tests.
- [ ] New diagnostic codes are in `DiagnosticCodes` and `docs/diagnostics.md`.
- [ ] Generated SQL uses only permitted extensions, each only when enabled in options (a unit
      test, added in WP 1.8, scans all golden `.pgsql` files: every `CREATE EXTENSION` names a
      permitted extension, and with `Extensions` empty the output contains none).
- [ ] Output is deterministic (a test in WP 1.8 converts the fixture twice and compares bytes).
- [ ] `CHANGELOG.md` entry under `## Unreleased`; `BACKLOG.md` updated if something was measured
      or learned.
- [ ] No content from other converters; no private schemas or data.
