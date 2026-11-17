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
- WP 3.2 — statements and control flow; script batches with variables become DO blocks; 6 both-engines message cases, 7 golden cases.
- WP 3.3 — functions (scalar, inline TVF, multi-statement TVF); corpus compile scan 0 apply failures on all six databases with functions converted.
- WP 3.4 — procedures (CREATE PROCEDURE or set-returning function, refcursor result sets, INOUT OUTPUT parameters and return_code, EXEC → CALL, INSERT … EXEC, transactions); 11 both-engines cases. Corpus compile scan: 0 apply failures on all six databases. Procedures converted / Manual: Northwind 7/0, pubs 4/0, WideWorldImportersDW 14/10, WideWorldImporters 10/32, AdventureWorks 2/8. Manual is mostly S2P0206, from TRY/CATCH, cursors, temp tables and dynamic SQL (WP 3.5–3.8).
- WP 3.5 — temp tables and table variables (temporary tables dropped when the routine ends, table variables per routine, OUTPUT … INTO, TRUNCATE, OBJECT_ID on temp tables); 11 both-engines cases, each run twice in one session. Corpus compile scan: 0 apply failures on all six databases. Procedures converted / Manual: WideWorldImportersDW 16/8 (was 14/10); WideWorldImporters 10/32 and AdventureWorks 2/8 unchanged, as each of those procedures also has TRY/CATCH, cursors or dynamic SQL. WideWorldImporters' S2P0206 count fell from 230 to 207.
- WP 3.6 — TRY … CATCH (exception blocks, ERROR_*() from stacked diagnostics with SQL Server error numbers, transaction patterns in TRY/CATCH, BEGIN TRAN → COMMIT); 9 both-engines cases. Corpus compile scan: 0 apply failures on all six databases. Procedures converted / Manual: AdventureWorks 3/7 (was 2/8), WideWorldImportersDW 17/7 (was 16/8), WideWorldImporters 10/32 (its TRY blocks are translated, but the procedures still have dynamic SQL and cursors).
- WP 3.7 — cursors (FOR loop for the OPEN/FETCH/WHILE pattern, refcursor variables otherwise, scrolling, @@FETCH_STATUS, WHERE CURRENT OF, cursor variables); 5 both-engines cases. Corpus compile scan: 0 apply failures on all six databases. WideWorldImporters' S2P0206 count fell from 214 to 154; its 32 Manual procedures all still contain dynamic SQL (WP 3.8).
- WP 3.8 — dynamic SQL (EXEC (string) and sp_executesql as EXECUTE, parameters through USING, OUTPUT through INTO, run-time names and values through format(), dynamic result sets); 7 both-engines cases. Corpus compile scan: 0 apply failures on all six databases. WideWorldImporters: S2P0206 154 → 105; 48 S2P5060, nearly all dynamic DDL (triggers, sequences, security policies), so its Manual count is unchanged.
- WP 3.9 — triggers (per-event trigger functions, AFTER … FOR EACH STATEMENT with transition tables, INSTEAD OF on views FOR EACH ROW, UPDATE(column) by value, ROLLBACK as an error); 1 both-engines case of eight statements. Corpus compile scan: 0 apply failures on all six databases. AdventureWorks triggers: 6 converted, 4 Manual (2 INSTEAD OF on tables, S2P6004); AdventureWorks procedures 7 converted / 3 Manual (was 3/7) after XACT_STATE() outside CATCH became 1; pubs' trigger converts.
- WP 3.10 — assessment v2 (effort weights, objects needing most work, `assess --html`), S2P5070 for DDL in routines, docs coverage test, procedure calls in result agreement. Corpus: 0 apply / copy / verify failures on all six databases; no S2P9001; no generic S2P0206 left (every Manual routine and trigger has a specific code); 190 agreement queries at baseline. Stage 3 (v0.3) complete.
- WP A.1 — CodeConversion library (edit engine, C#/VB literal reading and writing incl. verbatim and raw, input discovery for files, folders, projects and solutions), `sql2pg code` verb; 15 tests.

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
- Not translated yet (diagnostics say so): UPDATE/DELETE with the target inside an outer join, DML with TOP, FOR XML other than PATH('') concatenation, FOR JSON AUTO over joins, OUTPUT INTO together with a plain OUTPUT.
- The compile scan and the agreement harness share one migration per corpus database per test run (`CorpusMigration.DefaultAsync`); its PostgreSQL database is dropped with the container.
- An INSERT that leaves out a `rowversion` column fails on PostgreSQL (the column is a NOT NULL bytea with no default, S2P1103). Stage 3's trigger work should give it a default or a trigger.
- `Module` has `IsIndexed` (an indexed view), read from sys.indexes by the catalog reader and from CREATE CLUSTERED INDEX by the script reader.
- WP 3.2 departs from the plan in two places. `SELECT @v = … FROM …` that may return several rows is a FOR loop over the rows rather than `LIMIT 1`: the loop keeps SQL Server's last-row and no-rows behaviour exactly, and S2P5002 now warns only when there is no ORDER BY. The @@ROWCOUNT and identity helpers are `sql2pg_rowcount` and `sql2pg_identity`, not `v_rowcount` and `v_scope_identity`, which a T-SQL variable `@rowcount` would collide with.
- Established on SQL Server: PRINT and RAISERROR send an empty message (`PRINT ''`, `PRINT NULL`) as a single space; PRINT, RAISERROR and SET options reset @@ROWCOUNT to 0; SET @v = … sets it to 1.
- WP 3.4 departs from the plan in these places:
  - The refcursor parameters are `result_set`, or `result_set_1`, `result_set_2`, … when there
    are several. The plan wrote `<name>_rs`.
  - A single result set can sit anywhere in a procedure that becomes a function, not only at
    the end: `RETURN QUERY` adds the rows and carries on, as SQL Server does. Three more things
    keep it a procedure: a status code (a function with `RETURNS TABLE` cannot have INOUT
    parameters), `COMMIT`/`ROLLBACK`, and a result set inside a loop.
  - OUTPUT parameters get `DEFAULT NULL`, so a caller need not pass them.
  - Two codes are added: S2P5026 (BEGIN TRAN dropped, Info) and S2P5027 (EXEC of an unknown
    procedure, Error).
  - In `sql2pg translate`, transaction statements outside a DO block become `BEGIN`, `COMMIT`,
    `ROLLBACK`, `SAVEPOINT` and `ROLLBACK TO SAVEPOINT`.
- Established on PostgreSQL 18:
  - `RETURN QUERY` needs each column's type to match `RETURNS TABLE` exactly. The type
    modifiers are dropped from both, so each column is cast to the bare type (`varchar`,
    `numeric`, and `bpchar` for char).
  - `OPEN result_set FOR …` works on an INOUT refcursor parameter, and a CALL from PL/pgSQL
    can pass it on to another procedure.
  - A function may CALL a procedure that does not commit.
- Not translated yet in procedures: rows returned by DML with `OUTPUT` (S2P0206); a result set
  inside a loop; any statement other than a result set under `SET ROWCOUNT n`; `SET ROWCOUNT`
  inside IF/WHILE or with a variable; `INSERT … EXEC` of a procedure that returns cursors
  (S2P4003).
- WP 3.5 departs from the plan in these places:
  - `ON COMMIT PRESERVE ROWS` is not written, since it is PostgreSQL's default.
  - A temp table named like a real table is not renamed `tmp_…`. References to real tables are
    always schema-qualified, so a temp table cannot hide one.
  - A table variable is `tv_<routine>_<name>`, not `tv_<name>`. Nested procedures often declare
    table variables with the same name (`@t`, `@result`); with one name per session, the
    callee's `DROP TABLE IF EXISTS` would remove the caller's. Scripts use `tv_<name>`.
  - A table variable is created at the start of the body, not where its DECLARE is: T-SQL
    declares it once, so one declared inside a loop keeps its rows across iterations. A
    function's table variables are `ON COMMIT DROP`, as WP 3.3's return table is.
  - A procedure that returns refcursors does not drop its temp tables at the end: the cursors
    read them after the procedure has returned. They go at the next call or the end of the
    session.
  - `OUTPUT … INTO` works into any table, not only table variables. The CTE gets a column list
    (`sql2pg_output (c1, …)`), so unnamed or repeated OUTPUT columns are fine.
  - After a `ROLLBACK`, each table variable is created again when the rollback removed it.
  - Four codes are added: S2P5030 (global temp table), S2P5031 (ROLLBACK with table variables),
    S2P5032 (a callee creates a temp table of the caller's name) and S2P5033 (FOREIGN KEY on a
    temp table dropped).
  - Also done: `TRUNCATE TABLE` (with `RESTART IDENTITY`), `CREATE INDEX` and `DROP TABLE` on
    temp tables, `OBJECT_ID('tempdb..#t')`, and in `sql2pg translate` a batch with table
    variables but no other procedural code stays plain SQL rather than a DO block.
- Established on SQL Server 2022:
  - `SCOPE_IDENTITY()` and `@@IDENTITY` follow inserts into temp tables and table variables.
  - An `INSERT` of one or more rows into a table without an identity column sets both to NULL.
    An `INSERT` of no rows leaves them as they were. WP 3.2 had both wrong: it kept the value
    in the first case and set NULL in the second. Both are fixed.
  - A table variable keeps its rows when the transaction is rolled back.
- Established on PostgreSQL 18:
  - `DROP TABLE` fails ("being used by active queries in this session") on a table that an open
    cursor reads.
  - `MERGE … RETURNING` can be a data-modifying CTE.
  - `to_regclass('pg_temp.t')` finds the session's temp table.
  - `CREATE INDEX ON t (…)` without a name works.
- Not translated yet for temp tables and table variables:
  - `ALTER TABLE` on a temp table, and computed columns in one (S2P0206).
  - Under `CaseInsensitive = IcuCollation`, a temp table's text columns get the default
    collation, not `sql2pg_ci`.
  - S2P5032 looks only at procedures called directly, not further down the call chain.
  - A table variable declared after a `ROLLBACK` in the text is not created again after it.
  - A table variable makes a function `VOLATILE` and needs a writable transaction, so such a
    function fails in a read-only transaction or on a standby.
- Seen while testing WP 3.5, from WP 3.4: `BEGIN TRAN` is dropped, so a `ROLLBACK` in a
  procedure called outside a transaction also undoes what the procedure did before
  `BEGIN TRAN`. SQL Server had already committed that work. Resolved in WP 3.6: `BEGIN TRAN`
  is now `COMMIT`.
- WP 3.6 departs from the plan in these places:
  - The error helpers are `sql2pg_error_message`, `sql2pg_error_number` and so on (`_2`, `_3`
    for nested CATCH blocks), not `v_error_*`, which a T-SQL `@error_message` would collide
    with. ERROR_STATE() is 1, as THROW's state is not carried.
  - ERROR_PROCEDURE() is the schema-qualified name: SQL Server 2022 returns `dbo.p`.
  - The plan made every COMMIT/ROLLBACK in a TRY an error (S2P5042). The common patterns are
    translated instead:
    - `BEGIN TRAN` in a TRY commits before the block.
    - A `COMMIT` ending the TRY runs after the block.
    - The handler keeps its `ROLLBACK`.
    - `XACT_STATE()` and `@@TRANCOUNT` in CATCH are constants that say whether a `BEGIN TRAN`
      may have run.
  - `BEGIN TRAN` becomes `COMMIT` everywhere in a routine (it was dropped in WP 3.4).
  - Two codes are added: S2P5043 (ERROR_*() outside CATCH) and S2P5044 (a TRY that changes data
    outside a transaction, which PostgreSQL undoes whole).
- Established on PostgreSQL 18:
  - `COMMIT` inside a block with an exception handler fails ("cannot commit while a
    subtransaction is active"), and so does a CALL of a committing procedure from one ("invalid
    transaction termination").
  - `COMMIT` and `ROLLBACK` inside the handler itself work, and `RAISE;` after a `ROLLBACK` in
    the handler still re-raises the error.
  - Local variables keep the values the TRY gave them when the block rolls back.
  - `GET STACKED DIAGNOSTICS … PG_EXCEPTION_DETAIL` is an empty string for errors without a
    detail.
- Established on SQL Server 2022: ERROR_NUMBER() is 8134 for division by zero, 2627 for a unique
  violation, 547 for a check violation, 245 for a failed conversion and 50000 for RAISERROR with
  a message. ERROR_PROCEDURE() is schema-qualified.
- Not translated yet in TRY … CATCH:
  - COMMIT/ROLLBACK in the middle of a TRY, or EXEC of a committing procedure inside one
    (S2P5042).
  - A TRY that changes data outside a transaction loses the changes that SQL Server would keep
    (S2P5044, Warning only).
  - ERROR_*() in a procedure called from a caller's CATCH is NULL (S2P5043, Warning).
    AdventureWorks' uspLogError and uspPrintError do this. Passing the error on through
    transaction-local settings (`set_config('sql2pg.error_number', …, true)` in each handler) would
    fix it.
  - The message text of errors PostgreSQL raises (PostgreSQL's wording).
- WP 3.7 departs from the plan in these places:
  - The FOR loop reads into helper variables (`sql2pg_fetch1`, …) and copies them into the T-SQL
    variables, rather than reading `r.a` from a record.
  - FETCH reads into the same helpers, and assigns the T-SQL variables only when a row came back.
  - Cursors are unbound `refcursor` variables `cur_<name>`, opened with the query, not
    `DECLARE c CURSOR FOR` in the declaration section. PostgreSQL then names each portal itself,
    so the same cursor name in nested procedures does not clash.
  - S2P5050 is reported only for cursors declared `GLOBAL` explicitly. SQL Server's own default
    is GLOBAL, through the database option, but undeclared cursors are used locally in practice.
- Established on PostgreSQL 18:
  - A FOR loop over no rows, and a FETCH past the last row, set their target variables to NULL.
  - `WHERE CURRENT OF` works with a refcursor variable.
  - A refcursor variable can be opened again after CLOSE.
- Not translated yet for cursors:
  - `FETCH` without `INTO`, which returns the row as a result set.
  - `@@CURSOR_ROWS` and `CURSOR_STATUS()`.
  - Cursor OUTPUT parameters (`CURSOR VARYING OUTPUT`).
  - `DEALLOCATE` without `CLOSE` before a cursor is opened again.
  - The sensitivity of DYNAMIC and KEYSET cursors (PostgreSQL's see the rows as at OPEN).
- WP 3.8 departs from the plan in these places:
  - A string in a variable (`SET @sql = …; EXEC (@sql)`, the usual corpus form) is followed back
    to the assignment that last set it. The plan looked only at the EXEC's own argument.
  - Values between quotes use `%L`, and other spliced pieces `%s`, with positional specifiers
    (`%2$L`). The translation can move pieces, for example `TOP` becomes `LIMIT` at the end.
  - Names from QUOTENAME are mapped at run time: lower case and the schema map. NameStyle
    SnakeCase reports S2P5060 for them.
  - Rows from dynamic SQL are a result set (OPEN … FOR EXECUTE); a SELECT into OUTPUT parameters
    is EXECUTE … INTO.
  - Two codes are added: S2P5061 (a name mapped at run time, Info) and S2P5062 (text spliced as it
    stands, Warning).
- Established on PostgreSQL 18:
  - `EXECUTE` with `USING` runs a string holding several statements.
  - `format()` arguments can be numbered (`%1$I`).
- Established on both engines: a value with a quote spliced between quotes breaks SQL Server's
  dynamic SQL (a syntax error). PostgreSQL's `%L` quotes it and the query runs.
- Not translated yet for dynamic SQL:
  - DDL and control flow inside the string (most of WideWorldImporters' dynamic SQL builds
    triggers, sequences and security policies).
  - A string built with `+=` or in a loop.
  - EXEC of a procedure inside the string.
  - `EXEC (…) AT linked_server`.
  - In `sql2pg translate`, a dynamic SELECT at the top of a script (it goes to a DO block, which
    returns no rows).
- WP 3.9 departs from the plan in these places:
  - Each event gets its own trigger function, not one function behind several triggers. The
    pseudo-table an event lacks is an empty query of the table (`SELECT * FROM t WHERE false`).
    A single function would fail on a statement that names `deleted` in an INSERT firing.
  - `UPDATE(column)` compares the old and new values, joining `inserted` and `deleted` on the
    primary key. The plan had it always true. It is still S2P6001, a Warning, because
    `SET c = c` differs.
  - An INSTEAD OF trigger on a view takes any body, with `inserted` as `(SELECT NEW.*)`. The plan
    allowed a single DML statement only. A longer body reports S2P6003 as a Warning, not an
    Error.
- Established on PostgreSQL 18:
  - Transition tables need a single-event trigger.
  - A statement trigger fires for an UPDATE of no rows too, as SQL Server's does.
  - `(SELECT NEW.*) AS inserted` works in a row trigger on a view.
- Seen in the corpus with WP 3.9: all ten AdventureWorks triggers were Manual. Two are INSTEAD OF
  on tables (S2P6004). The other eight call `uspLogError`, which was Manual for testing
  `XACT_STATE()` outside CATCH (S2P5025). `XACT_STATE()` outside CATCH is now 1 (S2P5041, Info).
- Not translated yet for triggers:
  - `sp_settriggerorder` (only reported, S2P6006).
  - A trigger disabled in SQL Server (it is created enabled).
  - `UPDATE(column)` for `SET c = c`.
  - Triggers on tables that are not converted.
  - A trigger's reads of `@@ROWCOUNT` after its first statement follow the usual rules.
- WP 3.10:
  - Effort weights live in `DiagnosticCodes.Weight`: Info 0, Warning 1, Error 5. Overrides:
    encrypted and CLR objects 20, parse and internal errors 10, external references, dynamic SQL,
    INSTEAD OF on tables and DDL triggers 8, refcursor result sets and DDL in a routine 3, and a
    dependent's S2P0203 0.
  - The compile scan's `Untranslated` field groups the S2P0206 and S2P5060 messages by kind. Every
    S2P0206 left in the corpus was DDL in a routine; that is now S2P5070. WideWorldImporters'
    remaining items are ALTER TABLE SET SYSTEM_VERSIONING and dynamic DDL (triggers, security
    policies, roles); WideWorldImportersDW's are index and constraint changes around loads.
  - The agreement harness calls converted procedures. `pubs/32-proc-reptq3` is at 50: the CASE
    over a `char(12)` column keeps the trailing spaces in SQL Server (char to varchar), and
    PostgreSQL's bpchar to text conversion drops them. Four of the eight rows differ only by
    padding. The fix belongs in the expression translator: `rpad` a char(n) operand that a CASE
    or `+` turns into varchar. The semantic model also types that CASE as varchar(3), the
    literal's length; SQL Server's precedence rules give varchar(12).
  - AdventureWorks' `uspGetEmployeeManagers` and `uspGetManagerEmployees` are Manual (S2P3001,
    hierarchyid methods), so they are not in the agreement set.
