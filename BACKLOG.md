# Backlog

What was done and measured, as it lands. The plan is in
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Done

- WP 0.1 — solution skeleton.
- WP 0.2 — CI (`build.yml`) and release (`publish-nuget.yml`).
- WP 0.3 — golden runner, container fixtures, `BothEngines`, `CanonicalValue`.
- WP 1.1 — model, diagnostics, options, conversion result.
- WP 1.2 — catalog reader, `tests/fixtures/shop.sql` fixture, 37 reader tests.
- WP 1.3 — script reader; catalog/script agreement test on the shop fixture.
- WP 1.4 — name mapper with embedded PostgreSQL 18 keyword list.
- WP 1.5 — type mapper.
- WP 1.6 — schema expression translator, PgAst expression nodes, expression writer; 60 golden cases, 36 both-engines cases.
- WP 1.7 — PgAst DDL nodes and writer.
- WP 1.8 — schema translator, SchemaConverter, ScriptPlanner; 26 DDL golden cases; shop fixture applies from catalog and script.
- WP 1.9 — applier.
- WP 1.10 — data copier with range splitting, resume, identity/sequence continuation.
- WP 1.11 — data verifier (count and checksum).
- WP 1.12 — CLI; migrate of the shop fixture end to end through the CLI.
- Default target PostgreSQL 18; CI container job on 18 only.
- WP 1.13 — corpus download script, compile scan with positive control; Stage 1 (v0.1) exit criteria met on PostgreSQL 18 (also passed on 15, 16 and 17 before the default changed).
- WP 2.1 — semantic model (scopes, typing, signatures, view columns, dependencies).
- WP 2.2 — expression translator and function map; WP 2.6 — mssql_compat helpers (pulled forward: WP 2.2's tests need them).
- WP 2.3 — query and DML translator; 57 golden cases, 58 both-engines cases.
- WP 2.4 — views; corpus views 35 converted and applied, 10 Manual with specific codes.
- WP 2.5 — translate verb (ScriptTranslator).
- WP 2.7 — result-agreement harness; 180 corpus queries, 179 at 100. Stage 2 (v0.2) complete.
- WP 3.1 — PL/pgSQL AST and writer; every node compiled and run on PostgreSQL 18.

## Measured

Stage 1 compile scan (WP 1.13) on PostgreSQL 18, 2026-11-15. Objects by status; failures are
of objects that are not Manual.

| Database | Rows copied | Converted | With warnings | Manual | Skipped | Apply / copy / verify failures |
|---|---|---|---|---|---|---|
| AdventureWorks2022 | 760,837 | 415 | 2 | 9 | 62 | 0 / 0 / 0 |
| AdventureWorksDW2022 | 1,060,820 | 114 | 0 | 0 | 8 | 0 / 0 / 0 |
| WideWorldImporters | 4,713,833 | 294 | 5 | 30 | 50 | 0 / 0 / 0 |
| WideWorldImportersDW | 923,643 | 129 | 1 | 8 | 31 | 0 / 0 / 0 |
| Northwind | 3,308 | 73 | 0 | 0 | 23 | 0 / 0 / 0 |
| pubs | 255 | 42 | 0 | 4 | 6 | 0 / 0 / 0 |

Skipped objects are views, routines and triggers (Stage 2 and 3) and index kinds with no
PostgreSQL equivalent. Positive control (an identity typed as `date`) is reported.

After WP 2.2 (full expression translator in schema expressions), same scan, 2026-11-15: still
0 apply / copy / verify failures everywhere; Manual objects fell from 51 to 8.

| Database | Converted | With warnings | Manual | Skipped | Error diagnostics |
|---|---|---|---|---|---|
| AdventureWorks2022 | 418 | 2 | 6 | 62 | 6 |
| AdventureWorksDW2022 | 114 | 0 | 0 | 8 | 0 |
| WideWorldImporters | 318 | 11 | 2 | 50 | 2 |
| WideWorldImportersDW | 136 | 2 | 0 | 31 | 0 |
| Northwind | 73 | 0 | 0 | 23 | 0 |
| pubs | 46 | 0 | 0 | 6 | 0 |

Built-ins checked on both engines (WP 2.2): 180 cases over a value grid, all agreeing.

Result agreement (WP 2.7) on PostgreSQL 18, 2026-11-15: 30 queries per corpus database,
translated by `ScriptTranslator` and run on both engines; score = matching rows / rows × 100.
Baselines are in `tests/Majorsilence.Sql2Postgre.Corpus.Tests/Queries/Baselines.json`.

| Database | Queries | At 100 | Below 100 |
|---|---|---|---|
| AdventureWorks2022 | 30 | 30 | — |
| AdventureWorksDW2022 | 30 | 30 | — |
| WideWorldImporters | 30 | 30 | — |
| WideWorldImportersDW | 30 | 30 | — |
| Northwind | 30 | 30 | — |
| pubs | 30 | 29 | `29-like-range` 92.9: `LIKE '[A-M]%'` matches `del Castillo` under SQL Server's case-insensitive collation |

Found and fixed on the way (first run: 79 of 82 at 100): AVG of money/decimal result types and
truncation, real against exact numbers, real-to-numeric casts, windowed AVG of decimal.

## Notes for later

- A machine with only the .NET 10 SDK builds `net10.0` alone; `Directory.Build.props` adds
  `net11.0` when the SDK is 11 or later. CI installs both SDKs.
- Package versions are central (`Directory.Packages.props`).
- `CanonicalValue` lives in `src/…Data` rather than the integration tests (the plan's WP 0.3 placement), because WP 1.11's verifier must format values exactly the same way. Its tests are unit tests.
- First container run pulls the SQL Server 2022 image (~10 minutes on a cold machine); later runs start in seconds.
- Two contract renames from the plan: `Index` → `TableIndex` (it collided with `System.Index` under implicit usings) and the dependency tuple → `ObjectDependency` record (System.Text.Json does not serialize tuples). `Sequence` also carries `CurrentValue` for WP 1.10's `setval`.
- Readers return `SourceReadResult` (database + reader diagnostics), an addition to the plan's contracts.
- SQL Server stores defaults and checks in its own normalized form: `((1))`, `([qty]>(0))`, `([email] like '%@%')`. WP 1.6 parses these, so it must accept the extra parentheses and bracketed names.
- Constraint names SQL Server generates (`PK__…`, `FK__…`, `CK__…`) are null in the model (contract change: `PrimaryKey`/`UniqueConstraint`/`ForeignKey`/`CheckConstraint.Name` are `string?`). The DDL translator (WP 1.8) must name those constraints the way PostgreSQL does (`<table>_pkey`, `<table>_<cols>_fkey`, …).
- The script reader cannot know a computed column's type (SQL Server derives it); it records `SqlServerType("computed")`. WP 1.8 needs a type for a generated column: from the catalog it has one, from scripts it must come from WP 2.1's type inference — until then such columns need a `TypeOverrides` entry or are reported.
- `NameMapper` adds `Type(ObjectName)` beside the plan's methods: SQL Server keeps types in their own namespace, PostgreSQL in the relations' one, so a type and a table of the same name collide.
- Quoting covers reserved, type/function-name and column-name keywords (catcodes R, T, C): over-quoting a lower-case name is harmless (it is the same identifier).
- `vector(n)` is modelled as `SqlServerType("vector", Length: n)`. The catalog reader does not read it yet (SQL Server 2025 adds `sys.columns.vector_dimensions`; the test container is 2022).
- Established on both engines: CAST(decimal|float AS int) truncates in SQL Server and rounds in PostgreSQL (→ trunc()); CAST(money AS int) rounds in both; ROUND(float, n) differs for binary halves (S2P3012). Keep these in WP 2.2's FunctionMap.
- The expression half of `PgWriter` landed in WP 1.6 (golden tests need to print expressions); WP 1.7 adds the DDL half. `ScriptDomTypes` is a source file linked into SqlServer and Translation (neither may reference the other).
- Golden files are copied to the test output at build: after `SQL2PG_UPDATE_GOLDEN=1` creates a new expected file, rebuild before the normal run.
- Translation produces `TranslatedObject` (PgAst statements); `Postgres.SchemaConverter` writes them to text to make `ConvertedObject` — the writer lives in Postgres, which depends on Translation.
- A table with an untranslatable part (a default, a check, a computed column) is still valid DDL with that part commented out. WP 1.9's applier should apply Manual objects in the Tables/Constraints/Indexes/ForeignKeys phases (skipping them would break every foreign key pointing at the table) and skip Manual Code/Triggers objects by default — a deviation from the plan's `--apply-manual` default, to confirm with the maintainer.
- Disabled constraints are created NOT VALID with S2P2013 (PostgreSQL still enforces them for new rows); untrusted-but-enabled ones get S2P2004 (Info). The plan had one code for both.
- A computed column read from a script gets its type inferred from the expression (S2P2010) or, failing that, S2P2007 (Error).
- The catalog reader clamps decimal sequence bounds to bigint (a decimal(38,0) sequence would overflow the read).
- The script reader now reads CREATE SPATIAL/XML/FULLTEXT INDEX.
- PostgreSQL rounds a 7th fractional digit half to even (checked on 17: .0000005→0, .0000015→2, .0000025→2), not half away from zero as the plan guessed; the copier rounds the same way because binary COPY would truncate.
- `Sequence.CurrentValue` is now `last_used_value` (null when the sequence was never used), so setval is only issued for sequences that handed out values.
- PostGIS EWKB and pgvector binary writes are implemented and unit-tested at the byte level but not run against a server with those extensions (the test PostgreSQL has none). A CI job on the postgis/postgis image would close that.
- decimal/numeric values beyond System.Decimal's 28–29 digits fail the table's copy (SqlDataReader.GetDecimal overflows) with S2P7003; reading SqlDecimal into a BigInteger-scaled numeric would fix it.
- `--recreate` (drop and recreate the target) is on `apply` and `migrate`; it is never the default.
- `migrate` exits 1 when the reader found something a person must handle (an encrypted module) even if everything else succeeded.
- WideWorldImportersDW's restore once failed inside SQL Server recovery (internal error 0x82000007) right after WideWorldImporters was restored into the same container; it passed on the rerun and in four other runs. If it recurs, restore each corpus database into a fresh container or raise the container's memory.
- The S2P3001 errors left in the corpus (WideWorldImporters 35, WideWorldImportersDW 8, AdventureWorks 6, pubs 4) are expressions WP 2.2 should cover first: NEXT VALUE FOR in defaults, LIKE with [ ] character classes, CONVERT with styles, user-defined functions in computed columns.
- Corpus view typing (WP 2.1): AdventureWorks 163/258 view columns typed (the rest are xml .value()/.query() results), AdventureWorksDW 59/59, Northwind 102/102, WideWorldImporters 32/32, pubs 6/6.
- Unqualified names in a module resolve in the module's schema first, then dbo (a heuristic that matches the usual case; SQL Server's own rule depends on the owner's default schema).
- The Compat phase now comes before Tables (file 03-compat.sql; tables are 05-tables.sql): a default or computed column may call a helper, and PostgreSQL checks it exists at CREATE TABLE.
- Differences no translation fixes, found by the both-engines grid: string comparison, REPLACE and CHARINDEX follow the column's collation (SQL Server's defaults are case-insensitive; PostgreSQL's are not), and ORDER BY on text follows each server's collation (PostgreSQL's linguistic collations ignore leading spaces). Decimal division keeps more digits in PostgreSQL than SQL Server's result-scale rules give. CONCAT and implicit date-to-string conversions format datetimes differently.
- In generated columns, CONCAT of strings and numbers becomes coalesce(x, '') || …; concat(), to_char() and the TRY_CAST helpers are not immutable, so columns using them are held back (S2P2009).
- Not translated yet (diagnostics say so): UPDATE/DELETE with the target inside an outer join, DML with TOP, FOR XML other than PATH('') concatenation, FOR JSON AUTO over joins, OUTPUT INTO, variables (Stage 3).
- The compile scan and the agreement harness share one migration per corpus database per test run (`CorpusMigration.DefaultAsync`); its PostgreSQL database is dropped with the container.
- An INSERT that leaves out a `rowversion` column fails on PostgreSQL (the column is a NOT NULL bytea with no default, S2P1103). Stage 3's trigger work should give it a default or a trigger.
- `Module` has `IsIndexed` (an indexed view), read from sys.indexes by the catalog reader and from CREATE CLUSTERED INDEX by the script reader.
