# Roadmap: a SQL Server (T-SQL) to PostgreSQL converter

Majorsilence.Sql2Postgre reads a SQL Server database (live, or from scripts), converts its schema and code to PostgreSQL, copies its data, and proves the result
by running it. It is a set of .NET libraries and a `dotnet tool` CLI, laid out the way
[majorsilence.crystal](https://github.com/majorsilence/majorsilence.crystal) is: a neutral
model in the middle, a reader on one side, an emitter on the other, and progress measured
against a corpus rather than judged by eye.

This document is a plan, not a record. What was done and measured goes in `BACKLOG.md` as it
lands; every change gets a `CHANGELOG.md` entry under `## Unreleased`.

## What we are building

| Capability | Input | Output |
|---|---|---|
| **Assess** | A database or scripts | A report: every object, whether it converts cleanly, with warnings, or needs a person, and why |
| **Schema** | Tables, types, keys, indexes, sequences, defaults, computed columns | Ordered PostgreSQL DDL (pre-data / post-data) |
| **Code** | Views, functions (scalar, inline and multi-statement TVF), procedures, triggers | SQL and PL/pgSQL |
| **Ad-hoc SQL** | `.sql` files or strings (application queries, reports, scripts) | PostgreSQL SQL |
| **Data** | Every table | Rows in PostgreSQL via binary `COPY`, verified |
| **Verify** | The converted output | Applied to a real PostgreSQL; data and query results compared with SQL Server's |

**The output is pure, unmodified PostgreSQL.** Everything emitted runs on a stock community
PostgreSQL server: core features, plus a short list of permitted extensions — PostGIS,
pgvector, pgcrypto, pg_trgm, pg_cron, hstore, uuid-ossp and pg_stat_statements. Others (e.g.
`citext`, `ltree`, `tablefunc`) are considered case by case by the maintainer and are not used
until approved. No C functions, no forks or compatibility layers. A core construct is preferred
whenever one does the job; a permitted extension is emitted only when a converted object needs
it, and each can be switched off, in which case the converter falls back to core types or marks
the object for manual work.

Nothing is ever silently dropped. A construct the converter cannot translate is emitted as the
original T-SQL in a comment with a stable diagnostic code (`S2P1234`), and the object is
reported as needing manual work.

## Clean room: other converters are off limits

This repository is tri-licensed (MIT, Apache 2.0, BSD 3-Clause). Code, mapping tables, test
cases or documentation taken from another project would have to carry that project's terms,
which in practice none allows us to relicense under all three. So the rule is:

- **Do not read, copy or port** the source, tests, mapping tables or docs of other SQL Server →
  PostgreSQL converters, transpilers, loaders or compatibility layers, open source or not —
  unless their license explicitly lets us relicense under this repo's tri-license, and the
  maintainer has confirmed that in the PR.
- **What we may use:** the vendors' own product documentation (the SQL Server T-SQL reference,
  the PostgreSQL manual) as the description of each engine's behaviour, and the engines
  themselves, run in tests, as the arbiter of what a statement does. Every mapping in this repo
  is derived from those two and written here first.
- **Dependencies are not copying.** Referencing a package (ScriptDom, SqlClient, Npgsql) under
  its own license is fine; vendoring its source is not.
- The same holds for AI agents working in the repo; `AGENTS.md` will say so.

## Key building blocks (verified 2026-11)

| Package | Version | License | Use |
|---|---|---|---|
| `Microsoft.SqlServer.TransactSql.ScriptDom` | 180.x | MIT | **The T-SQL parser.** Microsoft's own, full-fidelity AST for every T-SQL version, with a visitor. This replaces the hand-written Irony grammar Crystal needed. |
| `Microsoft.Data.SqlClient` | 7.x | MIT | Reading the SQL Server catalog and streaming data |
| `Npgsql` | 10.x | PostgreSQL | Writing to PostgreSQL; `NpgsqlBinaryImporter` for binary `COPY` |
| `Testcontainers.MsSql` / `Testcontainers.PostgreSql` | 4.x | MIT | Real engines in tests |


**`.dacpac` input is deferred** (Stage 4). When it is picked up, it goes through
`Microsoft.SqlServer.DacFx` ([microsoft/DacFx](https://github.com/microsoft/DacFx)) as a package
dependency only, in its own optional project so the core stays tri-licensed. The NuGet package
ships under a Microsoft license file rather than MIT, so review that license, and how it
applies to redistributing a tool that depends on it, before starting. The clean-room rule
above applies to that repository's source as to any other.

**PostgreSQL targets:** 15 through 18 (14 reaches end of life on 2026-11-12). The minimum
matters: 15 gives `MERGE`, 16/17 widen it (`MERGE ... RETURNING` in 17), 18 adds virtual
generated columns and `LIKE` on nondeterministic collations. The emitter takes a target
version and uses the best construct it allows.

## Architecture

```
            ┌──────────────── Majorsilence.Sql2Postgre.SqlServer ───────────────┐
 live DB ──▶│ CatalogReader (sys.* views)  ─┐                                   │
 .sql   ───▶│ ScriptReader (ScriptDom)      ┴─▶ Model.SourceDatabase            │
            │                                   (+ object bodies as ScriptDom AST)│
            └───────────────────────────────────────┬────────────────────────────┘
                                                    ▼
            ┌──────────────── Majorsilence.Sql2Postgre.Translation ─────────────┐
            │ SemanticModel: symbol table of tables/columns/types/routines,     │
            │   expression type inference, dependency graph                      │
            │ TypeMapper · NameMapper · FunctionMap                              │
            │ Rewrite passes over the T-SQL AST (TOP→LIMIT, APPLY→LATERAL, …)    │
            │ Translators: Ddl · Query · Expression · Procedural (PL/pgSQL)      │
            │   └─▶ PgAst (small record tree)  ──▶ diagnostics (S2Pxxxx)         │
            └───────────────────────────────────────┬────────────────────────────┘
                                                    ▼
            ┌──────────────── Majorsilence.Sql2Postgre.Postgres ────────────────┐
            │ PgWriter (prints PgAst for a target version)                      │
            │ ScriptPlanner: pre-data / data / post-data, topological order     │
            │ Applier: runs scripts on a PostgreSQL, records each object's error │
            └───────────────────────────────────────┬────────────────────────────┘
                                                    ▼
            Majorsilence.Sql2Postgre.Data — parallel SqlDataReader → binary COPY,
            value transforms, identity/sequence reset, row-count and checksum verify
```

### Projects

| Project | Purpose |
|---|---|
| `Majorsilence.Sql2Postgre.Model` | Neutral model: databases, schemas, tables, columns, types, constraints, indexes, sequences, views, routines, triggers; `Diagnostic` with code, severity, object and source span |
| `Majorsilence.Sql2Postgre.SqlServer` | Reads SQL Server: catalog queries, module text (`sys.sql_modules`), dependencies (`sys.sql_expression_dependencies`), script files |
| `Majorsilence.Sql2Postgre.Translation` | The converter: semantic model, mappers, rewrite passes, translators, `PgAst` |
| `Majorsilence.Sql2Postgre.Postgres` | `PgWriter`, script planning and ordering, applying to a server |
| `Majorsilence.Sql2Postgre.Data` | Data copy and verification |
| `Majorsilence.Sql2Postgre.Compat` | An optional `mssql_compat` schema of helper functions (`datediff`, `datepart`, `isnumeric`, `charindex` with start, …) written in plain SQL/PL/pgSQL and created by the generated script itself, for constructs with no inline equivalent. Ordinary user functions, no extension |
| `Majorsilence.Sql2Postgre.Cli` | `sql2pg` dotnet tool |
| `tests/…Tests` | Unit tests: T-SQL snippet in, PostgreSQL out (golden files) |
| `tests/…Integration.Tests` | Testcontainers: SQL Server and PostgreSQL, apply and compare |
| `tests/…Corpus.Tests` | Sample databases end to end (see Measuring) |

Conventions carried over from Crystal: `net10.0;net11.0`, `.slnx`, `Directory.Build.props`
with `IsPackable=false` by default and packable projects opting in, NUnit, CI on Ubuntu and
Windows, tag `vX.Y.Z` → draft GitHub release → publish pushes to nuget.org with Trusted
Publishing, an `AGENTS.md` and a skill file setting out the method below.

### Why a small PostgreSQL AST instead of printing straight from the visitor

T-SQL and PL/pgSQL differ in structure, not just spelling: PL/pgSQL wants every `DECLARE` at
the top of a block, `TOP` moves from the front of a query to its end, a procedure that returns
rows becomes a function with a declared `RETURNS TABLE`, a table variable becomes a temp table
created before first use. Building a `PgAst` lets those be separate, testable passes, and lets
`PgWriter` print for a target version. Expressions that need no restructuring are translated
node for node.

### The semantic model is the hard core

Much of T-SQL means something only once types are known, which a text-level converter cannot
see:

- `a + b` is string concatenation (`||`) or addition depending on the operand types.
- `WHERE d = '20240101'` relies on implicit conversion PostgreSQL may not do the same way.
- `LEN(x)` ignores trailing spaces; `length(x)` does not.
- Comparisons under SQL Server's usual case-insensitive collation behave differently on a
  default PostgreSQL collation.

So translation runs against a symbol table built from the extracted schema (columns, types,
routine signatures, temp tables and variables declared in the body), with expression type
inference following SQL Server's data type precedence. Where a type can't be inferred, the
converter picks the likelier reading and emits a warning.

## Translation catalogue (the first cut)

### Types

| SQL Server | PostgreSQL | Notes |
|---|---|---|
| `bit` | `boolean` | Expressions `= 1` / `= 0` on bit columns rewritten |
| `tinyint` | `smallint` | + `CHECK (0..255)` optionally |
| `int`, `bigint`, `smallint`, `decimal`, `numeric`, `real`, `float(n)` | same / `double precision` | |
| `money`, `smallmoney` | `numeric(19,4)`, `numeric(10,4)` | Not `money` (locale-dependent) |
| `char`, `varchar(n)`, `nchar`, `nvarchar(n)` | `char`, `varchar(n)` | PostgreSQL is always Unicode |
| `varchar(max)`, `nvarchar(max)`, `text`, `ntext` | `text` | |
| `binary`, `varbinary`, `image` | `bytea` | |
| `datetime`, `smalldatetime`, `datetime2(n)` | `timestamp(3)`, `timestamp(0)`, `timestamp(n≤6)` | `datetime2(7)` loses the 7th digit: warn |
| `datetimeoffset` | `timestamptz` | The original offset is not kept: warn |
| `date`, `time` | `date`, `time` | |
| `uniqueidentifier` | `uuid` | `NEWID()` → `gen_random_uuid()` |
| `xml` | `xml` | XQuery methods (`.value()`, `.nodes()`) → `xpath()`; partial |
| `rowversion` / `timestamp` | `bytea` or dropped | Concurrency semantics need a trigger or `xmin`: manual |
| `sql_variant`, `hierarchyid` | `text` | Values as their string form; methods (`GetAncestor()` …) → manual |
| `geography`, `geometry` | PostGIS `geography`/`geometry`; `bytea` (WKB) with PostGIS off | Spatial methods → PostGIS `ST_*` in Stage 4 |
| `vector(n)` (SQL Server 2025) | pgvector `vector(n)`; `real[]` with it off | `VECTOR_DISTANCE` → pgvector operators |
| alias types (`CREATE TYPE … FROM`) | `DOMAIN` | |
| table types | composite type + array, or temp table | Table-valued parameters: manual-assisted |

### Schema

Identity → `GENERATED BY DEFAULT AS IDENTITY` (with `setval` after data load); sequences;
defaults (named default constraints become column defaults); computed columns → generated
columns (`STORED`, or `VIRTUAL` on 18 for non-persisted); PK/unique/FK (with `ON DELETE`/
`ON UPDATE`)/check constraints, `NOCHECK` constraints → `NOT VALID`; indexes: included columns
→ `INCLUDE`, filtered → partial, clustered → plain B-tree (optional one-time `CLUSTER`),
columnstore and XML/spatial/full-text indexes → assessed; indexed views → materialized views
(manual refresh, warned); synonyms → views or `search_path` notes; `dbo` → configurable
(`public` or kept).

### Queries and expressions

`TOP n` → `LIMIT`, `TOP … WITH TIES` → `FETCH FIRST … WITH TIES`, `TOP n PERCENT` → subquery;
`ISNULL` → `COALESCE`; `IIF` → `CASE`; `GETDATE()`/`SYSDATETIME()` → `localtimestamp`/
`clock_timestamp()`; `DATEADD` → interval arithmetic; `DATEDIFF`/`DATEPART`/`DATENAME` →
`extract` or compat functions (boundary-crossing semantics differ); `CONVERT(…, style)` →
`to_char`/`to_timestamp`; `CHARINDEX` → `strpos`; `LEN` → `length(rtrim())`; `+` on strings →
`||` (with `NULL` semantics checked); `CROSS APPLY`/`OUTER APPLY` → `[LEFT] JOIN LATERAL … ON
true`; `OUTPUT inserted.*` → `RETURNING`; `MERGE` → `MERGE` (≥15) or `INSERT … ON CONFLICT`;
`UPDATE … FROM` / `DELETE … FROM` joins restructured; `FOR XML PATH('')` string building →
`string_agg`; `FOR JSON` → `json_agg`/`jsonb_build_object`; `PIVOT`/`UNPIVOT` → conditional
aggregates / `LATERAL (VALUES …)`; `#temp` → `CREATE TEMP TABLE`; `@@ROWCOUNT` →
`GET DIAGNOSTICS`; `SCOPE_IDENTITY()` → `RETURNING … INTO`; `[bracketed]` names → the name map;
query hints (`NOLOCK`, `OPTION (…)`) → dropped with a note.

### Procedural code

Procedures → `PROCEDURE` (or `FUNCTION … RETURNS TABLE` when they return a result set, chosen
per procedure; see Decisions); scalar functions → `FUNCTION`; inline TVF → `LANGUAGE sql`
function `RETURNS TABLE`; multi-statement TVF → PL/pgSQL `RETURN QUERY`; `DECLARE @x` hoisted;
`SET`/`SELECT @x = …` → `:=` / `SELECT … INTO`; `IF`/`WHILE`/`BREAK`/`CONTINUE`; `TRY…CATCH` →
`BEGIN … EXCEPTION WHEN OTHERS`; `ERROR_MESSAGE()` etc. → `SQLERRM`/`GET STACKED DIAGNOSTICS`;
`RAISERROR`/`THROW` → `RAISE`; `PRINT` → `RAISE NOTICE`; `SET NOCOUNT ON` → dropped; cursors →
PL/pgSQL cursors or `FOR … IN` loops; `EXEC proc` → `CALL`/`SELECT`; dynamic SQL
(`sp_executesql`) → `EXECUTE format(…) USING`, translated when the string is a literal, assessed
otherwise; triggers → trigger function + trigger, with `inserted`/`deleted` mapped to
statement-level transition tables (`REFERENCING NEW TABLE AS inserted`); `INSTEAD OF` on views;
`GOTO`, CLR routines, cross-database and linked-server references → manual.

Behavioural differences that can't be converted away are reported, not hidden: an error inside
a PostgreSQL transaction aborts it (T-SQL often continues unless `XACT_ABORT` is on); functions
cannot commit; empty-string vs. `NULL` handling in some functions; identifier case.

## Data migration

- Load in three phases: pre-data (types, tables without FKs), data, post-data (indexes,
  constraints, FKs, triggers), so loads are fast and order-independent.
- Per table: `SqlDataReader` (sequential access) → `NpgsqlBinaryImporter`; tables in parallel,
  large tables split by primary-key range; batch size and degree of parallelism configurable.
- Value transforms: `bit` → `boolean`, strip or reject `\0` in strings (PostgreSQL refuses it),
  `datetimeoffset` → UTC, `datetime2(7)` rounding, `money`, `sql_variant` to text.
- After load: `setval` every identity/sequence to its max, `ANALYZE`.
- Verify: row counts per table, then per-table aggregate checksums computed the same way on
  both sides (opt-in, since it reads everything twice).
- Resumable: a state file records finished tables and ranges.
- Out of scope for the first releases: continuous replication / near-zero-downtime cutover
  (a later stage could read SQL Server Change Tracking or CDC).

## CLI

```
sql2pg assess   --source <conn|dir> [--html report.html] [--json report.json]
sql2pg schema   --source … --out <dir> [--target-version 17]
sql2pg translate <file-or-dir.sql> [-o <outdir>]       # ad-hoc SQL, no database needed
sql2pg data     --source <conn> --target <conn> [--tables …] [--parallel 4] [--verify]
sql2pg migrate  --source <conn> --target <conn>        # schema + data + post-data + verify
sql2pg verify   --source <conn> --target <conn> [--queries <dir>]
```

A JSON config file carries the choices a user makes once: name mapping (case, schema
renames), per-column type overrides, objects to skip, whether to use the compat schema, the
target version.

## Measuring

As in Crystal, progress is measured on a corpus, with a recorded baseline per case.

- **Corpus:** public sample databases only — AdventureWorks (OLTP and DW), WideWorldImporters,
  Northwind, pubs, and the samples in `microsoft/sql-server-samples` (MIT), downloaded by a
  script, never committed. Private databases may be measured locally but are reported as counts
  only: no names, objects, code or data in the repo, tests, commits or PRs.
- **Compile scan:** every object of every corpus database is converted and applied to
  PostgreSQL (each target version, in Testcontainers). Count of objects that fail to create:
  target 0 for anything not marked manual. A **positive control** (a deliberately wrong
  conversion) must still be caught, so a broken scan can't report a false 0.
- **Data agreement:** every corpus table copied and checksum-verified.
- **Result agreement:** a set of queries and procedure calls per corpus database, run on SQL
  Server and on PostgreSQL, result sets compared (order-insensitive unless `ORDER BY`),
  scored 0–100 per case with a baseline and a tolerance; an unexplained jump either way fails.
- **Unit tests:** every translation rule has golden-file tests that fail without it, and where
  practical a both-engines test that runs the T-SQL and the output and compares the answer.
  Behaviour is established by running SQL Server, not assumed.
- **Assessment detectors** (Crystal's `scan`): `sql2pg assess` over a corpus histograms every
  unsupported construct by diagnostic code, which is how the next stage's work is chosen.

## Stages

Each stage ships on its own with exit criteria.

### Stage 0 — scaffolding (1–2 days)

Solution, `Directory.Build.props`/`.targets`, the projects above as empty shells, CI matrix
(Ubuntu + Windows, net10/net11; Testcontainers jobs on Ubuntu), pack/release workflows,
`AGENTS.md`, a method skill file, `CHANGELOG.md`, `BACKLOG.md`, corpus download script.
**Exit:** `dotnet build` and an empty test run green in CI.

### Stage 1 — schema and data (v0.1, ~3–4 weeks)

Catalog reader, model, type/name mapping, DDL for everything under *Schema* above, script
planner with dependency order, applier, full data copy with verification, `assess` (schema
part), `schema`, `data`, `migrate`. Views/routines are listed in the assessment but not yet
converted.
**Exit:** every corpus database's tables, keys, indexes and data land in PostgreSQL 15–18 with
0 compile-scan errors and 100% data agreement.

### Stage 2 — queries, expressions and views (v0.2, ~4–6 weeks)

ScriptDom integration, semantic model and type inference, `FunctionMap`, rewrite passes,
`PgAst` + `PgWriter`, views, computed-column and check expressions converted properly,
`translate` for ad-hoc `.sql`, compat schema (first functions).
**Exit:** all corpus views compile; result agreement recorded for a query set per database;
every `FunctionMap` entry has a both-engines test.

### Stage 3 — procedural code (v0.3, ~6–8 weeks)

PL/pgSQL translator: functions (scalar, inline, multi-statement), procedures, triggers,
error handling, temp tables and table variables, cursors, literal dynamic SQL.
**Exit:** corpus routines and triggers compile (manual-marked excluded), result agreement for
procedure calls, every manual item carries a diagnostic code with documentation.

### Stage 4 — the long tail (ongoing)

Chosen from the assessment histogram: `FOR XML`/`FOR JSON` variants, `PIVOT`, XML methods,
`sys.*` catalog queries in code → `information_schema`/`pg_catalog`, spatial methods via
PostGIS, SQL Server Agent T-SQL jobs via pg_cron (opt-in), full-text → `tsvector`, table-valued parameters, collation strategy refinements, an HTML
assessment report, `.dacpac` input (see the DacFx note above), and possibly incremental data
sync.

## Decisions to settle before Stage 1

1. **Identifier case.** Fold to unquoted lower case (idiomatic PostgreSQL; application SQL
   keeps working when it doesn't quote) — recommended, with collision detection — or keep the
   original case and quote everything (exact, but every client query then needs quotes).
   Optional `snake_case`.
2. **Case-insensitive comparison.** SQL Server databases are usually `…_CI_AS`. Options: a
   nondeterministic ICU collation (`CREATE COLLATION … provider = icu, deterministic = false`,
   core PostgreSQL on any ICU-enabled build) on text columns — closest, `LIKE` support needs
   18 — or ignore and report. (`citext` is an extension, so it is out.) Recommended: ICU
   collation where the target allows, else report.
3. **Procedures that return result sets.** `FUNCTION … RETURNS TABLE` (callers use
   `SELECT * FROM f()`) or `PROCEDURE` with `refcursor` OUT parameters (closer to the call
   shape). Recommended: function by default, configurable per routine.
4. **Compat schema.** Ship `mssql_compat` helpers — plain PL/pgSQL functions the generated
   script creates, so still pure PostgreSQL, but extra objects in the target — or inline
   everything (no extra objects; some constructs become manual). Recommended: on by default,
   off by flag.
5. **Minimum PostgreSQL version.** Recommended 15.
