# Changelog

## Unreleased

- **xml methods are translated** over `XMLTABLE` (WP 4.3):
  - `value()`, `exist()` and `query()` in expressions, and `nodes()` in `CROSS`/`OUTER APPLY`.
  - Paths are turned into XPath 1.0. `declare namespace` becomes `XMLNAMESPACES`, and a default
    element namespace becomes a prefix on unprefixed element names.
  - `modify()` (S2P3040) and XQuery beyond XPath 1.0 (S2P3041) are reported.
  - Eight AdventureWorks XML views are new result-agreement queries.
- `CONVERT` from text with a date style reads year-first strings as SQL Server does: `yyyymmdd`;
  `yyyy-m-d` for datetime in the style's month/day order; and ISO 8601 with `T` for date and
  datetime2.
- **System-versioned temporal tables** (WP 4.2):
  - Both readers record a table's `PERIOD FOR SYSTEM_TIME` and history table.
  - The schema gets two row triggers per table: one sets the period columns, the other copies
    old rows into the history table (S2P2020).
  - `FOR SYSTEM_TIME AS OF | FROM … TO | BETWEEN | CONTAINED IN | ALL` reads both tables with
    SQL Server's period rules (S2P4014).
  - In routines, `SET (SYSTEM_VERSIONING = OFF | ON)` and `DROP | ADD PERIOD` disable and enable
    those triggers (S2P5075).
- **DDL inside routines is translated** for the forms load procedures use (WP 4.1):
  - `CREATE INDEX`, `DROP INDEX`, and `ALTER INDEX … REBUILD|REORGANIZE|DISABLE`. REBUILD creates
    a dropped (disabled) index again from its definition (S2P5071, S2P5072).
  - `NOCHECK`/`CHECK CONSTRAINT` drop the constraint and add it back `NOT VALID`, with
    `VALIDATE` under `WITH CHECK` (S2P5073).
  - `ALTER TABLE … ADD`/`DROP` of columns and constraints, and `ALTER COLUMN`.
  - `DROP PROCEDURE`/`DROP FUNCTION` become `DROP ROUTINE` (S2P5074).
  - Columnstore, xml and full-text index DDL is reported as S2P2003, as in the schema.
  - The corpus scan lists S2P5070 by statement, and fails on any internal error (S2P9001).
- Plan: Stage 4 broken into WPs 4.1–4.16, ordered by what the corpus scan and BACKLOG still
  report. `.dacpac` input and PostgreSQL 15–17 wait for the maintainer.
- **`sql2pg code` converts Entity Framework Core.**
  - `UseSqlServer` becomes `UseNpgsql`.
  - Column types, default SQL and computed SQL go through the schema converter's mappings, and
    identity columns and the default schema are converted.
  - SQL Server-only fluent calls are removed (S2P8040).
  - A loop in `OnModelCreating` names the model as the converted schema does, lower case with
    `DateTime` as `timestamp` (S2P8041).
  - SQL Server-only `EF.Functions`, temporal tables and migrations are reported (S2P8042,
    S2P8043).
  - Project files and `Directory.Packages.props` swap the SQL Server packages for Npgsql's at the
    matching versions (S2P8044).
  - A DbContext gives the same results on the migrated database after conversion. This
    completes Stage A. (WP A.6)
- **`sql2pg code` and Dapper.Contrib.**
  - `[Table]` names become the converted table names.
  - Entities whose property names differ from the converted columns are reported (S2P8030):
    Contrib's PostgreSQL adapter quotes them in Insert, Update and Delete.
  - Dialect overrides are reported (S2P8031).
  - Dapper queries, procedure calls and Contrib's Get, GetAll, Insert, Update and Delete run on
    both engines with the same results. (WP A.5)
- **`sql2pg code` converts stored procedure calls** (ADO.NET `CommandType.StoredProcedure`,
  Dapper `commandType:`).
  - The procedure's name and the names of the parameters passed become the converted ones.
  - A procedure converted to a function is read with `SELECT * FROM f(…)`.
  - A procedure is CALLed by Npgsql, with Output parameters as they are.
  - Refcursor procedures and calls whose form is unknown are reported (S2P8020, S2P8021).
  - Calls in C# and VB give the same results on the migrated database. (WP A.4)
- **`sql2pg code` translates SQL in code.**
  - It covers ADO.NET command text, Dapper's query and execute methods, and EF Core's raw SQL.
  - The text is followed back to literals, constants and once-assigned locals, translated from
    T-SQL with parameters kept, and written back in the literal's own form. EF's `{0}`
    placeholders and `FromSql` interpolation holes are kept.
  - `INSERT …; SELECT SCOPE_IDENTITY()` becomes `INSERT … RETURNING`.
  - `int` COUNT and SUM results are cast to integer.
  - `EXEC` with parameters becomes CALL or a function call.
  - SQL built at run time and procedural SQL are reported (S2P8010–S2P8013).
  - A program with T-SQL in its commands gives the same output on the migrated database after
    conversion. (WP A.3)
- **`sql2pg code` converts ADO.NET.**
  - SqlClient namespaces and types become Npgsql's, and `SqlDbType` becomes `NpgsqlDbType`
    following the type mapper.
  - `SqlException` becomes `NpgsqlException`, with known error numbers compared as SQLSTATEs.
  - SQL Server connection strings become Npgsql's.
  - What has no counterpart is reported: `SqlBulkCopy`, table-valued parameters, `SqlTypes`,
    query notifications, `InfoMessage` (S2P8001–S2P8008).
  - In C# and VB.NET. A sample program in each language prints the same on PostgreSQL after
    conversion as on SQL Server before it. (WP A.2)
- **`sql2pg code`: the start of the application code converter (Stage A).**
  - A new library, `Majorsilence.Sql2Postgre.CodeConversion`, parses C# and VB.NET with Roslyn.
    Rules record edits, and the edits are applied to the original text, so untouched code
    (comments, formatting, line endings, encoding) stays as it was.
  - `sql2pg code <path>` takes a file, a folder, a project or a solution, and writes converted
    copies to `--out` or rewrites files `--in-place`.
  - The verb has `--json` and `--source`. See `docs/code-conversion.md`.
  - The conversion rules follow in WPs A.2–A.6. (WP A.1)
- Plan: Stage A, a Roslyn-based converter for C# and VB.NET application code (ADO.NET, SQL in
  code, stored procedure calls, Dapper, Dapper.Contrib, Entity Framework Core), as WPs A.1–A.6.
- **Assessment v2.**
  - `sql2pg assess` estimates the effort, in points: Info 0, Warning 1, Error 5. Encrypted and
    CLR code, dynamic DDL, INSTEAD OF on tables, DDL triggers and external references weigh more.
    An object waiting on another weighs nothing, since its work is counted on that object. It
    also lists the objects that need the most work.
  - `--json` adds the effort, in total and per object.
  - `--html report.html` writes one self-contained HTML file: the summary, effort by kind,
    diagnostics by code and a sortable, filterable table of every object with its messages.
  - DDL inside a procedure, function or trigger reports its own code, S2P5070 (it was S2P0206).
  - A test checks that every diagnostic code has its section in `docs/diagnostics.md`.
  - The corpus agreement harness calls converted procedures: 10 calls on Northwind, pubs and
    AdventureWorks, 9 of them agreeing fully.
  - The compile scan records what is left untranslated, by kind. (WP 3.10)
- **Triggers are converted.**
  - Each event of a DML trigger is a trigger function and a `CREATE TRIGGER`. An AFTER trigger
    is `FOR EACH STATEMENT` with the transition tables `inserted` and `deleted`, so set-based
    bodies run unchanged. An INSTEAD OF trigger on a view is `FOR EACH ROW` over `NEW`/`OLD`.
  - `@@ROWCOUNT` is the number of rows the statement affected.
  - `UPDATE(column)` tests whether the value changed, joining on the primary key (S2P6001).
  - `ROLLBACK` raises an error that ends the statement.
  - These report a code: `COLUMNS_UPDATED()` (S2P6002); INSTEAD OF on a table (S2P6004); DDL and
    logon triggers (S2P6005); several triggers on one event (S2P6006, name order); a multi-statement
    INSTEAD OF body (S2P6003).
  - Statements fired on both engines leave the same rows. (WP 3.9)
- **Dynamic SQL is converted.**
  - `EXEC (string)` and `sp_executesql` become `EXECUTE`. The text is translated as T-SQL; when
    it is held in a variable, it is followed back to the SET or DECLARE that gave it its value.
  - sp_executesql parameters are `$1`, `$2`, … with `USING`, and a `SELECT @out = …` into an
    OUTPUT parameter is `EXECUTE … INTO`.
  - Pieces from variables go through `format()`:
    - `QUOTENAME(@t)` is a name (`%I`), mapped at run time as the converter maps names, schema
      map included.
    - A value between quotes is `%L`.
    - Other text is spliced in as it is (`%s`, S2P5062).
  - A dynamic SELECT's rows are the procedure's result set, through its refcursor.
  - Dynamic SQL whose text is not known, or that holds DDL or control flow, reports S2P5060.
  - The forms run on both engines with the same rows and OUTPUT values. (WP 3.8)
- **Cursors are converted.**
  - The usual `OPEN`, `FETCH NEXT … INTO`, `WHILE @@FETCH_STATUS = 0` loop becomes
    `FOR … IN query LOOP`.
  - Other cursors are `refcursor` variables, opened with their query at `OPEN`, with every
    `FETCH` direction, `@@FETCH_STATUS`, `CLOSE`, and `WHERE CURRENT OF` (the query gets
    `FOR UPDATE`).
  - Cursor variables (`SET @c = CURSOR FOR …`) are converted too.
  - FETCH past the last row leaves the variables as they were, as in T-SQL.
  - `GLOBAL` cursors report S2P5050.
  - The loop, scrolling, `WHERE CURRENT OF`, cursor variables and nested cursors run on both
    engines with the same results. (WP 3.7)
- **TRY … CATCH is converted.**
  - It becomes a block with an `EXCEPTION WHEN OTHERS` handler. The handler fills
    `ERROR_MESSAGE()` and `ERROR_NUMBER()` with `GET STACKED DIAGNOSTICS`. `ERROR_NUMBER()` maps
    common SQLSTATEs to SQL Server's numbers (2627, 547, 515, 8134, 245 and others) and keeps
    `THROW`'s own number.
  - `ERROR_SEVERITY()` is 16, `ERROR_PROCEDURE()` is the schema-qualified name,
    `ERROR_LINE()` is NULL and `ERROR_STATE()` is 1 (S2P5040). Outside CATCH, `ERROR_*()` is
    NULL. This reports S2P5043, a Warning: an error-logging procedure that reads its caller's
    error sees NULL.
  - `XACT_STATE()` and `@@TRANCOUNT` in CATCH are constants: whether a `BEGIN TRAN` may have run
    (S2P5041).
  - A `BEGIN TRAN` inside TRY commits the work before the block, and a `COMMIT` ending the TRY
    runs after it. `ROLLBACK` and `COMMIT` in CATCH are kept. The usual
    `BEGIN TRAN … COMMIT … CATCH … ROLLBACK; THROW` procedures convert and end their
    transactions as SQL Server does.
  - Other transaction control inside TRY, or a call of a procedure that ends a transaction,
    reports S2P5042. A TRY that changes data outside a transaction and then runs more
    statements reports S2P5044: PostgreSQL undoes all of it on an error, and SQL Server keeps
    what was already done.
  - Changed: `BEGIN TRAN` in a procedure now becomes `COMMIT` rather than being dropped (S2P5026).
    SQL Server had already committed the work before it, so a later `ROLLBACK` must not undo
    that work.
  - A procedure that calls one that ends a transaction is no longer converted to a function.
  - Error numbers, nested handlers and the transaction patterns run on both engines with the
    same results and the same committed data. (WP 3.6)
- **Temp tables and table variables are converted.**
  - `CREATE TABLE #t` and `SELECT … INTO #t` create a PostgreSQL temporary table `t`. Columns,
    keys, `CHECK` constraints and indexes are kept, and `CREATE INDEX` and `DROP TABLE` on a temp
    table are translated.
  - A routine drops a temp table before creating it, and again when it ends or returns. A
    procedure that returns refcursors keeps its temp tables, which the cursors still read.
  - `DECLARE @t TABLE` is a temporary table `tv_<routine>_<name>`, or `tv_<name>` in a script.
    It is created once at the start of the body, as T-SQL declares it once, and dropped when a
    procedure or script batch ends. A function's is `ON COMMIT DROP`.
  - A script batch that only uses table variables, temp tables and plain statements stays plain
    SQL rather than a `DO` block, so its result sets reach the caller.
  - `OUTPUT … INTO` a table, temp table or table variable becomes a data-modifying CTE with
    `RETURNING`, and an `INSERT` that writes its rows. This works for INSERT, UPDATE, DELETE and
    MERGE.
  - `TRUNCATE TABLE` becomes `TRUNCATE TABLE … RESTART IDENTITY`.
  - `OBJECT_ID('tempdb..#t')` becomes `to_regclass('pg_temp.t')`.
  - `SCOPE_IDENTITY()` and `@@IDENTITY` follow inserts into temp tables and table variables.
    - Fixed: an insert of rows into a table without an identity column now sets them to NULL,
      as SQL Server does.
    - Fixed: an `INSERT … SELECT` that inserts no rows now leaves them unchanged.
  - Creating a temp table makes a procedure converted to a function `VOLATILE`. Reading one no
    longer counts as immutable.
  - New codes:
    - S2P5030: a global temp table (`##t`).
    - S2P5031: `ROLLBACK` in a routine with table variables, whose rows SQL Server keeps.
    - S2P5032: a procedure calls one that creates a temp table of the same name.
    - S2P5033: a `FOREIGN KEY` on a temp table is dropped, as SQL Server ignores it too.
  - Temp tables and table variables are created by sql2pg and run twice in one session on both
    engines with the same rows. (WP 3.5)
- **Stored procedures are converted.**
  - A procedure becomes `CREATE PROCEDURE`. OUTPUT parameters are `INOUT … DEFAULT NULL`, and
    T-SQL defaults are kept.
  - A procedure whose one result set is its only output becomes `FUNCTION … RETURNS TABLE`,
    with `RETURN QUERY` and each column cast to its declared type. "Only output" means no
    OUTPUT parameters, no status code and no COMMIT or ROLLBACK, and the option
    `ResultSetStyle` must be `Function`.
  - Other procedures return their result sets through INOUT refcursor parameters (`result_set`,
    or `result_set_1`, `result_set_2`, …) opened with `OPEN … FOR`. This reports S2P5021, since
    callers change.
  - `RETURN n` sets an INOUT `return_code` parameter, which starts at 0 (S2P5022).
  - `EXEC p …` inside converted code becomes `CALL p(name => value, …)`:
    - `@v OUTPUT` is passed as is and gets the new value back. Any other value for an OUTPUT
      parameter goes through a helper variable, as SQL Server does not copy it back.
    - `EXEC @rc = p` reads the status code.
    - A call to a procedure that returns cursors passes the caller's own cursors on.
    - A procedure converted to a function is read as `SELECT * FROM f(…)`, and that is how
      `INSERT … EXEC` is converted.
    - Calls to system procedures (`sp_*`, `xp_*`) report S2P5023, and calls to unknown
      procedures report S2P5027.
  - Transactions inside a procedure:
    - `BEGIN TRAN` is dropped (S2P5026).
    - `COMMIT` and `ROLLBACK` are kept.
    - `SAVE TRAN` and `ROLLBACK` to a savepoint report S2P5024, and `@@TRANCOUNT` reports
      S2P5025.
  - `SET ROWCOUNT n` before the result sets of a procedure becomes `LIMIT n`.
  - A procedure that calls a procedure that is not converted is Manual (S2P0203), as views and
    functions already were.
  - `sql2pg translate`:
    - An `EXEC` with constant arguments is a top-level `CALL` or `SELECT * FROM f(…)`.
    - `BEGIN TRAN`, `COMMIT`, `ROLLBACK`, `SAVE TRAN` and `ROLLBACK` to a savepoint become
      `BEGIN`, `COMMIT`, `ROLLBACK`, `SAVEPOINT` and `ROLLBACK TO SAVEPOINT`.
  - Procedures of every shape are created by sql2pg and run on both engines with the same
    result sets, OUTPUT values, status codes and committed data. (WP 3.4)
- **User-defined functions are converted.**
  - A scalar function whose body is one `RETURN` becomes a `LANGUAGE sql` function with a
    SQL-standard `RETURN` body; any other scalar function is PL/pgSQL.
  - An inline table-valued function is `RETURNS TABLE (…) LANGUAGE sql` over its query. If a
    result column's type is unknown, the function is Manual with S2P5010.
  - A multi-statement table-valued function fills a temporary table `_ret_<name>` (dropped
    first, `ON COMMIT DROP`, with the declared keys) and returns its rows. INSERT, UPDATE and
    SELECT on its `@table` go to that temporary table.
  - Volatility comes from the body: VOLATILE when it writes, STABLE when it reads tables, calls a
    function that is not immutable or works with dates and times, and IMMUTABLE otherwise.
  - Parameters keep their names. A parameter after a defaulted one gets `DEFAULT NULL`
    (S2P5020).
  - Calls to converted routines convert each argument to the parameter's type as SQL Server
    does, since PostgreSQL will not narrow a bigint to an int to find the function. A trailing
    `DEFAULT` argument is left out; a `DEFAULT` before other arguments switches the rest to
    named notation.
  - A view or function that uses a function or view that is not converted is Manual
    (S2P0203); views that use converted functions now convert.
  - Fixed: INSERT, UPDATE and DELETE on a temporary table wrote its name as `"".name`.
  - Functions of every kind are created by sql2pg and called from queries on both engines with
    the same rows, including a multi-statement TVF called through CROSS APPLY. (WP 3.3)
- **Procedural T-SQL is translated to PL/pgSQL: variables, SET, SELECT assigning variables,
  IF/ELSE, WHILE/BREAK/CONTINUE, RETURN, PRINT, RAISERROR, THROW, WAITFOR DELAY, @@ROWCOUNT,
  SCOPE_IDENTITY()/@@IDENTITY, and the DML and queries between them.** `sql2pg translate` turns a
  batch that uses variables or control flow into a `DO` block.
  - Variables become `v_<name>`, all declared at the top of the body. The body starts with
    `#variable_conflict use_column`, so a bare name is a column, as in T-SQL. Leading DECLAREs
    keep their initial values in the declaration; a later DECLARE becomes an assignment at its
    place.
  - Values are converted on assignment as SQL Server converts them: a decimal assigned to an
    int is truncated, a string assigned to a shorter varchar is cut, and a bit prints as 1.
  - `SELECT @v = … FROM …` keeps SQL Server's meaning. A loop over the rows leaves the last
    row's values, and a query with no rows leaves the variables unchanged. Accumulating
    assignments (`SELECT @s = @s + name …`) are computed row by row. A plain `SELECT … INTO` is
    used where it gives the same answer: an aggregate, or at most one row (a unique key or
    TOP 1) into variables not yet assigned.
  - `@@ROWCOUNT` is read with `GET DIAGNOSTICS` after each statement, and is reset where T-SQL
    resets it (PRINT, RAISERROR, SET). The last identity comes from `RETURNING` on the INSERT, or
    the largest identity value inserted for a multi-row INSERT. The helper variables are named
    `sql2pg_rowcount` and `sql2pg_identity` (not `v_…`), so they cannot clash with a T-SQL variable.
  - New diagnostics S2P5001–S2P5009 and S2P5011. Statements not translated yet (EXEC,
    TRY/CATCH, cursors, temporary tables, transactions) stay as comments with S2P0206.
  - Checked on both engines: batches print the same messages, in the same order, on SQL Server
    and PostgreSQL 18. (WP 3.2)
- **PL/pgSQL syntax tree and writer, the base for translating procedures, functions and
  triggers.** Nodes for blocks (label, declarations, cursors, exception handlers), assignment,
  IF/ELSIF/ELSE, WHILE, LOOP, FOR over a range, a query or a dynamic query, EXIT/CONTINUE,
  RETURN/RETURN QUERY/RETURN NEXT, RAISE (the writer checks placeholders against arguments),
  ASSERT, PERFORM, EXECUTE … INTO … USING, GET [STACKED] DIAGNOSTICS, OPEN/FETCH/MOVE/CLOSE,
  COMMIT/ROLLBACK, CALL with named arguments, and SQL statements with INTO; plus
  `PgCreateRoutine` for functions and procedures with PL/pgSQL, dollar-quoted SQL or
  SQL-standard `RETURN` bodies. Bodies are dollar-quoted with `$$`, or `$body$`, `$body1$`…
  when the body contains the tag. Nested statements are indented without changing multi-line
  string literals. Routines built from every node are created and run on PostgreSQL 18 in the
  integration tests. (WP 3.1)
- **Fixed: an untranslated original that contains `/*` is written `/ *` inside its comment.**
  PostgreSQL nests block comments, so the comment never ended.

- **Result agreement on the public corpus: 180 T-SQL queries written for this repo (30 per
  corpus database: joins, aggregates, dates, strings, TOP, APPLY, CTEs, windows, ROLLUP/CUBE,
  PIVOT, views) are translated with `ScriptTranslator` and run on the SQL Server corpus database
  and on its PostgreSQL 18 migration; each scores matching rows / rows × 100.**
  `tests/…Corpus.Tests/Queries/Baselines.json` holds each query's expected score: any drop fails,
  and so does a rise of more than 5 until the baseline is updated (`SQL2PG_UPDATE_BASELINES=1`).
  179 of 180 agree exactly; the one that does not is a LIKE that SQL Server's case-insensitive
  collation matches more widely (92.9). The compile scan and the harness share one migration per database, and CI's
  container job now downloads the corpus (cached) and runs both. (WP 2.7)
- **Fixed, found by the harness: AVG of money is money and AVG of decimal(p, s) is
  decimal(38, max(s, 6)), both truncated as SQL Server truncates; SUM of real adds in float; an
  exact number compared with or combined with a `real` is converted to real, as SQL Server does
  (PostgreSQL compared in double precision, where real's 0.1 is greater than 0.1); a real cast
  to decimal or money goes through double precision (PostgreSQL's real-to-numeric cast keeps 6
  significant digits); a windowed AVG of money or decimal translates.**
  `ScriptTranslation` now has `Prelude` (the helpers) and `Body` beside `Sql`.
- **`sql2pg translate <file-or-folder> [--out dir] [--source conn|dir]` converts ad-hoc T-SQL
  scripts — application queries, reports, maintenance scripts — to PostgreSQL, writing a
  `.pgsql` and a `.diag.json` per `.sql`.** Queries and DML translate against the source's schema
  (types unknown without one); GO, USE and session SET options are dropped (`S2P0204`); SET
  IDENTITY_INSERT OFF becomes a setval; DDL (`S2P0205`) and procedural statements (`S2P0206`)
  stay as comments; mssql_compat helpers the script needs are created at its top so it runs on
  its own. (WP 2.5)
- **Views convert to PostgreSQL: CREATE VIEW (with its column list and WITH CHECK OPTION),
  indexed views as materialized views (`S2P2015`, they need refreshing), in dependency order.**
  WITH SCHEMABINDING is dropped (`S2P2016`), as is TOP 100 PERCENT … ORDER BY (`S2P4012`); a
  view that uses a routine or an unconverted view waits for Stage 3 (`S2P0203`). Every view that
  converts in the six corpus databases (35 of 45) applies on PostgreSQL 18. (WP 2.4)
- **SELECT, INSERT, UPDATE, DELETE and MERGE translate to PostgreSQL: TOP (WITH TIES, PERCENT),
  OFFSET/FETCH, APPLY as LATERAL joins, CTEs (recursive too), ROLLUP/CUBE/GROUPING SETS, set
  operations, EXISTS/IN/ANY/ALL subqueries, PIVOT and UNPIVOT, STRING_SPLIT, FOR XML PATH('')
  concatenation as string_agg (XML-escaped unless TYPE), FOR JSON PATH as json_agg, SELECT INTO
  (temp tables known to later statements), UPDATE/DELETE … FROM with joins rearranged as
  PostgreSQL wants them, and OUTPUT as RETURNING with PostgreSQL 18's old/new.** Hints are
  dropped with `S2P4002`. 58 queries and DML statements give the same rows on both engines
  against a copy of the shop fixture migrated by sql2pg. (WP 2.3)
- **T-SQL expressions translate through one translator that knows names and types: string,
  date, number, JSON, hashing, aggregate and window functions, CONVERT styles and FORMAT
  patterns, implicit conversions SQL Server makes and PostgreSQL does not, `bit` as `boolean`,
  `+` as `||` on strings, LIKE character classes as SIMILAR TO, NULLs sorted as SQL Server sorts
  them, and `NEXT VALUE FOR`.** Built-ins with no inline equivalent call `mssql_compat` helpers
  (DATEDIFF, CHARINDEX, ISNUMERIC, ISJSON, ISDATE, TRY_CAST, DATEPART(week)), plain SQL and
  PL/pgSQL created by the generated script in a new Compat phase before the tables. 180 cases
  agree on both engines, which settled (among others) that empty strings convert to 0 and
  1900-01-01, that STUFF past the end and ASCII('') are NULL, that AVG of integers truncates and
  that POWER keeps its first argument's type. Schema expressions use it too: untranslatable
  objects in the corpus fell from 51 to 8, with no new failures. (WP 2.2, WP 2.6)
- **`SemanticModel` knows what every name is and what type every expression has: tables, views
  (columns inferred from their SELECT, views over views included), table-valued function result
  columns, scalar function return types, procedure parameters, alias types; query scopes with
  aliases, derived tables, CTEs (recursive too), VALUES and APPLY, resolved inside out; expression
  types by SQL Server's data type precedence; dependencies from each module's parsed body.**
  References to another database or a linked server are reported as `S2P0301`. Every module of
  every corpus database parses, and every view resolves. (WP 2.1)
- **All six public sample databases (AdventureWorks, AdventureWorksDW, WideWorldImporters,
  WideWorldImportersDW, Northwind, pubs) migrate to PostgreSQL 18 with no apply, copy or
  checksum failures — about 7.5 million rows — and a deliberately wrong mapping is caught.**
  `scripts/download-corpus.sh` fetches them (SHA-256 checked) into the git-ignored
  `tests/corpus/`. The scan found and fixed: keys and indexes on computed columns that could not
  be translated (now held back with `S2P2014`), two unnamed checks on one table both named
  `<table>_check` (now `_check`, `_check1`), and indexes on `VIRTUAL` generated columns. (WP 1.13)
- **PostgreSQL 18 is the default target, and the only one tested for now** (`targetVersion`
  defaults to 18; tests and CI run against `postgres:18`). The Stage 1 switches for 15–17 stay;
  support for those versions is revisited once 18 is in good shape. A non-persisted computed
  column that is indexed or part of a key is created `STORED`, since PostgreSQL 18 cannot index a
  `VIRTUAL` one.
- **`sql2pg` has its verbs: `assess` (summary of objects by kind and status, the most frequent
  diagnostics, `--json` report), `schema` (phase scripts to `--out`), `apply`, `data`
  (`--tables`, `--parallel`, `--state`, `--verify count|checksum`) and `migrate` (tables,
  data, then code, indexes, constraints, foreign keys and triggers, then verification).**
  Connection strings come from options or `SQL2PG_SOURCE`/`SQL2PG_TARGET` and passwords are
  masked in every message. Exit codes: 0 success, 1 completed with Manual objects, failures or
  unverified tables, 2 usage error, 3 unexpected error. (WP 1.12)
- **`DataVerifier` checks copied tables by row count, or by an order-independent checksum
  (each row's canonical values hashed with XxHash64 and summed modulo 2^64, with the source
  values first put through the copier's own transforms).** A changed value fails the checksum
  (`S2P7102`) and a missing row the count (`S2P7101`). (WP 1.11)
- **`DataCopier` copies every converted table from SQL Server to PostgreSQL with binary `COPY`,
  several tables at once and big integer-keyed tables in key ranges, then continues identities
  and sequences where SQL Server left them and analyzes the tables.** Values cross over by
  documented rules (bit to boolean, datetimeoffset to UTC, 7th fractional digit rounded half to
  even as PostgreSQL rounds, NULs removed with `S2P7001`, spatial as WKB/EWKB, hierarchyid and
  sql_variant as text); a state file lets a rerun skip finished tables and ranges, and anything
  unfinished is cleared first so rows are never doubled. Every edge value of every type arrives
  as expected. (WP 1.10)
- **`PgApplier` runs converted objects (or a directory of phase scripts) against PostgreSQL,
  each in its own transaction, and reports what applied, failed (with SQLSTATE) or was skipped;
  `StopOnError` stops at the first failure and `RecreateDatabaseAsync` drops and recreates the
  target.** Manual tables, constraints and indexes are applied (their DDL is complete with the
  untranslatable part as a comment); Manual views, routines and triggers only with
  `ApplyManual`. (WP 1.9)
- **A database's schema converts to ordered PostgreSQL scripts: extensions, schemas, domains,
  composite types, sequences, tables (identity, generated columns, defaults, optional ICU
  collation), then primary keys, unique and check constraints, indexes and foreign keys, written
  as `01-schemas.sql` … `10-triggers.sql` plus `report.json`.** System-named constraints get
  PostgreSQL-style names (`<table>_pkey`, `<table>_<cols>_fkey`), untrusted constraints are
  `NOT VALID`, unsupported index kinds are skipped with a warning, and anything untranslatable
  stays as a comment. The shop fixture applies to PostgreSQL from both readers. (WP 1.8)
- **The PostgreSQL DDL the converter emits has a tree and a writer: schemas, extensions,
  collations, domains, composite types, sequences, tables (identity, generated, defaults,
  collations), primary keys, unique, check and foreign-key constraints (`NOT VALID`), indexes
  (`INCLUDE`, `WHERE`, `USING`), and untranslated T-SQL kept as comments that cannot close
  early.** Non-stored generated columns are `VIRTUAL` on PostgreSQL 18 and `STORED` before. (WP 1.7)
- **Defaults, check constraints, computed columns and index filters translate to PostgreSQL:
  literals, the table's columns, operators (with `+` on strings as `||` and `bit` compared with
  `true`/`false`), `CASE`/`IIF`, `CAST`/`CONVERT` without a style, and `GETDATE`,
  `SYSDATETIME`, `GETUTCDATE`, `NEWID`, `NEWSEQUENTIALID`, `ISNULL`, `UPPER`/`LOWER`/`LTRIM`/`RTRIM`,
  `ABS`, `ROUND`.** Casting a decimal or float to an integer type truncates as SQL Server does
  (PostgreSQL rounds), established by running both engines. Anything else is `S2P3001`. (WP 1.6)
- **`TypeMapper` maps every SQL Server column type to a PostgreSQL type: core types, PostGIS
  for spatial and pgvector for `vector` when enabled (`bytea` / `real[]` otherwise), domains
  for alias types, per-column overrides, and a diagnostic wherever values or behaviour change**
  (7th fractional digit, datetimeoffset offsets, rowversion, sql_variant, hierarchyid,
  case-insensitive collations). Every mapped type of the fixture creates on PostgreSQL. (WP 1.5)
- **`NameMapper` turns SQL Server names into PostgreSQL identifiers: lower case (or snake_case,
  or preserved), quoted only when PostgreSQL needs it (keywords taken from PostgreSQL 18's own
  `pg_get_keywords()`), shortened with a hash past 63 bytes (`S2P1001`), and given `_2`,
  `_3` on collisions per PostgreSQL namespace (`S2P1002`).** It answers the same way for every
  reference to a name, matching SQL Server's case-insensitive lookup. (WP 1.4)
- **`SqlServerScriptReader` reads a folder of `CREATE` scripts into the same model as the
  catalog reader, filling in what SQL Server fills in (`dbo`, default lengths, precisions and
  scales, `float(24)` as `real`, implicit NOT NULL, clustered primary keys).** A batch that does
  not parse is reported as `S2P0001` with its file and line, and reading continues. An
  integration test creates the shop fixture on SQL Server and checks both readers agree. System-
  generated constraint names are now read as null by both readers. (WP 1.3)
- **`SqlServerCatalogReader` reads a live SQL Server database into the model: tables, every
  column type, identity, defaults, computed columns, keys, foreign keys, checks, indexes
  (filtered, included, columnstore, full-text), sequences, alias and table types, modules with
  their text, and dependencies.** Encrypted modules and CLR objects are listed with
  `S2P0101`/`S2P0102`. Before this nothing could read a database. (WP 1.2)
- **The shared types exist: the SQL Server model (`SourceDatabase` and its tables, columns,
  constraints, indexes, sequences, types and modules), diagnostics with stable codes,
  `ConversionOptions` (JSON round trip, validation, permitted-extension list) and the
  conversion result.** Before this the projects were empty. (WP 1.1)
- **Golden-file tests and both-engines tests have a harness: a T-SQL case and its expected
  PostgreSQL output under `tests/*.Tests/Golden`, and one shared SQL Server and PostgreSQL
  container that run a query each and compare canonical results.** Before this there was no way
  to state a translation or an engine's behaviour as a test. (WP 0.3)
- **CI builds and tests on Ubuntu and Windows, runs the container tests against PostgreSQL 15,
  16, 17 and 18, and drafts a release with the packages on a `v*` tag.** Publishing that
  release pushes to nuget.org with Trusted Publishing. Before this there was no CI. (WP 0.2)
- **The solution builds and packs: seven packages (Model, SqlServer, Translation, Postgres,
  Data, Compat and the `sql2pg` tool) and three test projects.** Before this the repository held
  only the plan. (WP 0.1)
