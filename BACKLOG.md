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
- WP 4.24 — xml.modify() (`SET col.modify('…')` and `SET @x.modify('…')` as an assignment of the new mssql_compat helper `xml_modify`, which finds the target with `xpath`, counts the elements or text nodes before it and splices the document's text there: `insert` of one element constructor `as first into`, `as last into`, `into`, `before`, `after`; `replace value of` a text node, an attribute without a prefix or a typed element, with strings, numbers, `sql:column`, `sql:variable`, `data(path)` and `+ - * div` typed as XQuery types them and written as SQL Server writes each type; the readers keep the XML schema collections, whose element types are read with System.Xml; S2P3050 Info; fixed for every trigger: `UPDATE(col)` of xml and json compares the text, and a computed column a trigger names is STORED on 18 and 19); 1 golden file added and 1 changed, a both-engines grid of 57 modify() cases compared by text, a both-engines run of AdventureWorks' two triggers. Unit: 641. Integration: 824 on each of 15–19, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all five versions, the same each time: 223 passed, 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100 (on 15, one scan's teardown failed to drop its database, which was still in use; it passed on rerun). AdventureWorks: Manual 2 → 0 (`iuPerson`, `iduSalesOrderDetail`).
- WP 4.23 — AdventureWorks' last Manual objects (IMMUTABLE user-defined functions a default or computed column calls created with the tables, before the table, with the functions they call; INSTEAD OF triggers on tables as BEFORE … FOR EACH ROW triggers returning NULL, with a transaction-local guard so the body's own statements on the table pass through; ROLLBACK in a trigger remembered and raised when the trigger returns; @@TRANCOUNT in a trigger 1, or 0 after its ROLLBACK; IF and WHILE conditions with a CASE at their top level in parentheses; S2P6004 now a Warning); 2 golden files added and 3 changed, a both-engines run of AdventureWorks' trigger patterns and of a computed column calling a function. Unit: 627. Integration: 766 on each of 15–19, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all five versions, the same each time: 223 passed, 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. AdventureWorks: Manual 6 → 2 (`Sales.Customer`, `AK_Customer_AccountNumber`, `dEmployee`, `dVendor`; the two left are `xml.modify()`).
- WP 4.22 — the remaining small gaps (TRY_CONVERT with a style: a date to text as CONVERT, text to datetime2 or date under 126/127 through the new IMMUTABLE helper try_convert_iso8601, styles in parentheses accepted by CONVERT and TRY_CONVERT; with IcuCollation, CHARINDEX lower-cased on the default collation on every version, REPLACE and STRING_SPLIT as case-insensitive regular expressions before 18, the regular expressions in ISNUMERIC and try_convert_iso8601 on the default collation; HISTORY_RETENTION_PERIOD and typed xml read from the catalog and scripts and reported; S2P2028, S2P3049); 3 golden files, 33 both-engines cases (27 TRY_CONVERT inputs, 5 case-insensitive searches, the readers on retention and typed xml). Unit: 625. Integration: 765 on each of 15–19, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all five versions, the same each time: 223 passed, 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. WideWorldImporters: Manual 9 → 7 (`Sales.Invoices` and `IX_Sales_Invoices_ConfirmedDeliveryTime`, which leaves out an `nvarchar(4000)` included column, S2P2026). AdventureWorks: S2P2027 0 → 6 (its typed xml columns).
- WP 4.21 — syntax the translator read past (a reflection inventory of the ScriptDom node types the source names against the property names it uses, each candidate probed; named windows as PostgreSQL's WINDOW clause, TRIM LEADING/TRAILING as ltrim/rtrim, TABLESAMPLE n PERCENT as TABLESAMPLE SYSTEM, ADD … NULL DEFAULT without WITH VALUES as ADD then SET DEFAULT, all of which were wrong or dropped without a word; WITH XMLNAMESPACES and EXEC … AS USER reported; HIDDEN, masked and Always Encrypted columns read from the catalog and scripts and reported; SET options that change results a Warning, and reported in dynamic SQL; a unit test with a reviewed list of 87 unread properties; S2P2027, S2P3048, S2P4017, S2P5063); 7 golden files, 7 both-engines cases (named windows ×2, TRIM, TABLESAMPLE ×2, ADD … DEFAULT with and without WITH VALUES, HIDDEN and masked columns read). Unit: 622. Integration: 733 on each of 15–19, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all five versions, the same each time: 223 passed, 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. WideWorldImporters: S2P2027 0 → 10 (the five masked bank-account columns of `Purchasing.Suppliers` and `Suppliers_Archive`, whose masking had been dropped silently). Every other count as after WP 4.20.
- WP 4.20 — PostgreSQL 19 (`targetVersion` 15–19, 18 still the default; CI on all five; `FIRST_VALUE`, `LAST_VALUE`, `LAG` and `LEAD` with `IGNORE NULLS` for 19, `LAG`/`LEAD` with an offset or a default by SQL Server's rule, reported before 19 instead of dropped; included columns that could overflow a btree row left out, widest first; the keyword list checked on 15–19; a PostGIS test image built for 19; S2P2026, S2P3047); 3 golden files, 7 both-engines cases (6 window-function queries, a 3800-character included value copied and inserted). Unit: 614. Integration: 727 on each of 15–19, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all five versions, the same each time: 223 passed (three scans on 16 timed out and passed on rerun), 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. AdventureWorks: S2P2026 0 → 1 (`IX_ProductReview_ProductID_Name` leaves out `Comments`; on 19 it failed to apply before the fix). Every other count as after WP 4.19.
- WP 4.19 — roles, catalog views and server feature checks (sys.database_principals and sys.database_role_members over pg_roles and pg_auth_members; CREATE ROLE, ALTER ROLE … ADD/DROP MEMBER and WITH NAME, DROP ROLE in routines and dynamic SQL, role names kept as written; sys.indexes from a new mssql_compat.index_names table; IFs on SERVERPROPERTY values PostgreSQL answers with constants decided at conversion, the branch that never runs kept as a comment; specific S2P3043 messages for audits, partitioning, files, full-text catalogs, logins and the like; S2P3046, S2P5077); 1 golden file added and 1 changed, 3 both-engines cases (roles created, given a member and dropped, with names new per run; sys.indexes for keys, a filtered index and a heap; feature checks). Unit: 609. Integration: 720 on each of 15–18, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all four versions, the same each time: 223 passed (two PostGIS scans on 15 timed out and passed on rerun), 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. WideWorldImporters: Manual 14 → 9 (procedures 11 → 6), S2P3043 26 → 12, S2P5060 23 → 5. WideWorldImportersDW: Manual 5 → 0, S2P3043 6 → 0, S2P5060 25 → 0.
- WP 4.18 — DDL in dynamic SQL (constants folded into dynamic SQL text; CREATE PROCEDURE/FUNCTION/TRIGGER in it translated as the object it creates, registered in the semantic model so callers resolve; DROP TRIGGER, ALTER SEQUENCE … RESTART and CREATE/DROP SECURITY POLICY as row-level security, in routines and dynamic SQL; SESSION_CONTEXT, STRING_ESCAPE json, CONVERT 6/7/106/107; S2P3045, S2P5076); 2 golden files, 6 both-engines cases of procedures run twice (trigger, created procedure, security policy read as an ordinary role, sequence, session context) and 7 new function-grid cases. Integration: 717 on each of 15–18, 0 failed (on 15 and 16 the MERGE … OUTPUT case is skipped by design). Corpus on all four versions, the same each time: 223 passed, 0 apply / copy / verify failures, no S2P9001, 206 of 207 agreement queries at 100. WideWorldImporters: Manual 21 → 14 (procedures 17 → 11, functions 1 → 0), S2P5060 99 → 23, S2P5070 17 → 0, S2P5027 1 → 0. WideWorldImportersDW: Manual 7 → 5, S2P5060 26 → 25.
- WP 4.17 — PostgreSQL 15, 16 and 17 (18 stays the default; `--target-version`; OUTPUT without RETURNING old/new before 18, S2P4015; WHEN NOT MATCHED BY SOURCE as CTEs before 17, S2P4016; LIKE and SIMILAR TO on the ICU case-insensitive collation; container tests convert for SQL2PG_PG_VERSION, CI on 15–18); 5 golden files and 11 both-engines cases (4 OUTPUT, 7 ICU LIKE), plus the pg_cron test image fixed. Integration: 704 tests, 0 failed on each version; all pass on 18 and 17, and on 15 and 16 one case (a procedure with MERGE … OUTPUT, S2P4015) is skipped by design. The Agent-job test, skipped below 18 in the full runs because Testcontainers could not build the pg_cron image, passed on all four after the Dockerfile fix. Corpus on all four: 223 passed (15's two WideWorldImporters scans passed on a rerun after a container read timeout), 0 apply / copy / verify failures, no S2P9001, the same object counts per database, 206 of 207 agreement queries at 100; only S2P2001 differs (STORED computed columns: AdventureWorks 3 → 8, WideWorldImporters 1 → 4 below 18).
- WP 4.16 — incremental data sync (data --since: a whole first pass, then rowversion, change tracking or whole copies per table, upserted from a staging table with tracked deletes applied; S2P7010); a container test of three passes over a rowversion table, a change-tracked table and a plain one, verified by checksum. Stage 4 complete.
- WP 4.15 — SQL Server Agent jobs (--agent-jobs reads msdb jobs with T-SQL steps in the database; each becomes a sql2pg_agent procedure of its steps; cron.schedule for each daily/weekly/monthly schedule; S2P2023, S2P2024, S2P2025); 21 unit tests (schedule mapping, the synthesised procedure) and a container test on PostgreSQL 18 with pg_cron: a real msdb job converted, scheduled in cron.job, and its procedure leaving the same log as its steps on SQL Server. Corpus: unchanged (it does not read Agent jobs).
- WP 4.14 — trigram indexes (gin_trgm_ops indexes for columns searched with a leading-wildcard LIKE in converted views and routines, dynamic SQL included, and for columns the trigramIndexes option or --trigram-index names; S2P2022); 2 golden files (one changed) and a PostgreSQL 18 test that the index is created and used. Corpus: unchanged (it runs without pg_trgm).
- WP 4.13 — remaining DML forms (UPDATE/DELETE TOP (n) and outer-join targets through ctid key queries; FOR XML RAW, AUTO over one table and PATH as xmlelement/xmlagg); 3 golden files changed or added and 24 both-engines cases (11 DML, 13 FOR XML). Corpus: 0 apply / copy / verify failures and no S2P9001; no count changed (the corpus has none of these forms outside dynamic SQL).
- WP 4.12 — error context through calls (CATCH blocks that call code publish ERROR_NUMBER/MESSAGE/PROCEDURE in sql2pg.error_* settings, republished after COMMIT/ROLLBACK, cleared at the end; ERROR_*() outside CATCH reads them); 1 golden file and 5 both-engines cases of a logging procedure called from CATCH (THROW, divide by zero, RAISERROR, ROLLBACK before logging, no error). Corpus: 0 apply / copy / verify failures and no S2P9001; AdventureWorks' 13 S2P5043 Warnings are Info, and uspLogError is Converted (was ConvertedWithWarnings).
- WP 4.11 — connection strings in configuration files (appsettings*.json with comments, web.config/app.config connectionStrings and appSettings, providerName; database projects left alone; S2P8051, S2P8052); 3 golden files and 2 unit tests (no secret in a diagnostic; folder discovery).
- WP 4.10 — catalog queries (sys.objects/tables/views/procedures/sequences/schemas/columns and INFORMATION_SCHEMA TABLES/VIEWS/COLUMNS/ROUTINES/SCHEMATA as mssql_compat views under SQL Server names, over a converter-filled table of SQL Server and PostgreSQL names; OBJECT_ID, OBJECT_NAME, SCHEMA_NAME, SCHEMA_ID, COL_LENGTH, COLUMNPROPERTY, OBJECTPROPERTY, DB_ID, DB_NAME, IS_ROLEMEMBER, SERVERPROPERTY; S2P3043, S2P3044); 1 golden file and 21 both-engines cases. Corpus: 0 apply / copy / verify failures and no S2P9001. S2P3001: WideWorldImporters 30 → 3, WideWorldImportersDW 4 → 0. S2P3043 (catalog views not answered: sys.indexes, partitions, audits, principals): 25 and 6. S2P5060 rose (WideWorldImporters 48 → 99, WideWorldImportersDW 0 → 26) because the configuration procedures now translate past their catalog checks to the dynamic DDL they run; Manual counts are unchanged.
- WP 4.9 — char semantics (char values padded with rpad where SQL Server makes them strings, padding dropped again in comparisons; CASE/COALESCE/IIF string typing by SQL Server's rules; non-Unicode LIKE on char through mssql_compat.char_like); 1 golden file and 70 both-engines cases. Corpus: 0 apply / copy / verify failures and no S2P9001; pubs/32-proc-reptq3 at 100 (was 50).
- WP 4.8 — spatial methods with PostGIS (geography and geometry methods and properties as ST_* functions with postgis enabled, constructors, spatial_text for SQL Server's WKT; S2P3031; fixes to the PostGIS data copy and verify, and to spatial/xml index columns in the catalog reader); 2 golden files and a both-engines grid of 23 queries over points, lines and polygons on postgis/postgis:18-3.6, migrated, copied and verified first. Corpus: 0 apply / copy / verify failures and no S2P9001 on all six databases; with postgis, AdventureWorks, WideWorldImporters and WideWorldImportersDW also migrate with 0 failures (14 geography columns as PostGIS types, 6.4 million rows copied and verified as EWKB). The corpus has no spatial method outside dynamic SQL.
- WP 4.7 — full-text search (full-text indexes as GIN indexes of one to_tsvector expression per column in its language; CONTAINS and FREETEXT per column through the contains_query and freetext_query helpers; CONTAINSTABLE and FREETEXTTABLE as key/rank derived tables with top_n; LANGUAGE through fulltext_config; SERVERPROPERTY('LCID'); S2P2021, S2P3042; bit parameter defaults as booleans); 1 golden file and 46 both-engines cases on a SQL Server image with Full-Text Search, including that PostgreSQL uses the index. Corpus: 0 apply / copy / verify failures and no S2P9001 on all six databases; AdventureWorks' 3 full-text indexes created (Production.Document's without its varbinary documents, S2P2021), Manual procedures 1 → 0 (uspSearchCandidateResumes converts), S2P3001 6 → 1 (the UDF in a computed column).
- WP 4.6 — JSON (FOR JSON AUTO over joins with SQL Server's nesting and grouping, FOR JSON procedure results as one column, OPENJSON with and without WITH, JSON_MODIFY keeping member order, ISJSON with a type); 1 golden file and 20 both-engines cases. Corpus: 0 apply / copy / verify failures; 5 new WideWorldImporters agreement queries (the search procedures, OPENJSON and JSON_MODIFY over CustomFields), all at 100; WideWorldImporters Manual procedures 22 → 17. Agreement now compares JSON values parsed.
- WP 4.5 — table-valued parameters (READONLY table-type parameters as composite arrays read through unnest in procedures and inline functions; DECLARE @t TableType as a table variable passed on as an array; S2P8003 guidance for application code); 1 golden file and a both-engines run called from T-SQL, from SqlClient DataTables and from Npgsql mapped-composite arrays. Corpus: 0 apply / copy / verify failures; WideWorldImporters Manual procedures 24 → 22 (InsertCustomerOrders and InvoiceCustomerOrders convert; RecordColdRoomTemperatures is natively compiled).
- WP 4.4 — hierarchyid (mssql_compat helpers for GetLevel, GetAncestor, IsDescendantOf, GetDescendant, GetReparentedValue; depth-first hierarchyid_key for ORDER BY and comparisons; GetRoot, Parse, ToString; helper-calling computed columns STORED); 1 golden file and a both-engines grid of 23 cases (8 queries, 15 GetDescendant pairs). Corpus: 0 apply / copy / verify failures on all six databases; 4 new AdventureWorks agreement queries (both recursive manager procedures, the org chart in hierarchy order, the document tree), all at 100. AdventureWorks: Manual procedures 3 → 1, S2P3001 10 → 6.
- WP 4.3 — xml methods (value, exist, query over XMLTABLE; nodes() in CROSS/OUTER APPLY; XQuery prolog namespaces and default element namespace to XPath 1.0 prefixes; modify() S2P3040, XQuery beyond XPath 1.0 S2P3041), plus CONVERT reading year-first strings under other date styles as SQL Server does; 1 golden file, 7 xml both-engines cases, 5 new CONVERT cases. Corpus: 0 apply / copy / verify failures on all six databases; 8 new AdventureWorks agreement queries over its XML views, all at 100; AdventureWorks views 20 converted, 0 Manual (was 8 Manual). AdventureWorks' S2P3001 left is hierarchyid (WP 4.4), CONTAINSTABLE (WP 4.7) and a UDF in a computed column.
- WP 4.2 — system-versioned temporal tables (period and history in both readers; period and history row triggers; FOR SYSTEM_TIME in all five forms over both tables; SYSTEM_VERSIONING and PERIOD switches in routines; S2P2020, S2P4014, S2P5075); 1 golden file and a both-engines run of updates, deletes, every FOR SYSTEM_TIME form and a load around switched-off versioning. Corpus: 0 apply / copy / verify failures and no S2P9001 on all six databases. WideWorldImporters: 17 temporal tables converted, 37 FOR SYSTEM_TIME reads translated, S2P5070 85 → 17 (DROP TRIGGER of dynamically made triggers), Manual procedures 31 → 24.
- WP 4.1 — DDL inside routines (index DDL, NOCHECK/CHECK CONSTRAINT, ADD/DROP of columns and constraints, ALTER COLUMN, DROP PROCEDURE/FUNCTION → DROP ROUTINE; S2P5071–S2P5074); 1 golden file and a both-engines run of three procedures, each run twice. Corpus: 0 apply / copy / verify failures and no S2P9001 on all six databases; the compile scan now fails on S2P9001 and breaks S2P5070 down by statement. S2P5070: WideWorldImportersDW 37 → 0, WideWorldImporters 105 → 85. The 85 left are 68 temporal-table statements (WP 4.2) and 17 DROP TRIGGER of triggers made by dynamic SQL. Manual procedures are unchanged, because their other errors remain: dynamic DDL in WideWorldImporters, and catalog functions in WideWorldImportersDW (WP 4.10).
- WP A.6 — Entity Framework Core (UseNpgsql, column types, default and computed SQL, identity, SQL Server-only calls removed, naming loop in OnModelCreating, EF.Functions and migrations reported, project file package swap); 4 golden files and a both-engines DbContext run. Stage A complete: the C# and VB samples give the same output on PostgreSQL after conversion.
- WP A.5 — Dapper and Dapper.Contrib (`[Table]` names, S2P8030/S2P8031); 2 golden files and a both-engines run of Dapper queries, a procedure call and Contrib CRUD.
- WP A.4 — stored procedure calls (ADO.NET and Dapper; function shape read with SELECT, procedures CALLed, parameters renamed); 2 golden files and a C#/VB both-engines run.
- WP A.3 — SQL in code (`CommandTranslator`: parameters mode, INSERT … RETURNING for SCOPE_IDENTITY, int COUNT/SUM casts, EXEC with parameters; `SqlInCodeRule` for ADO.NET, Dapper and EF Core sites in C# and VB); 6 translator tests, 2 golden files, and a program run on both engines after migrating the database and converting the code.
- WP A.2 — ADO.NET rule (types, SqlDbType, SqlException numbers to SQLSTATEs, connection strings, reports); 4 golden files (C# and VB), and a C# and a VB sample compiled with Roslyn and run on both engines with the same output.
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
- PostGIS EWKB and pgvector binary writes are implemented and unit-tested at the byte level. The PostGIS path now runs against postgis/postgis in the container tests and the corpus (WP 4.8); pgvector's still runs against no server.
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
- WP A.2 departs from the plan in one place: `SqlException` becomes `NpgsqlException`, not
  `PostgresException`. NpgsqlException is the base class, and like SqlException it also covers
  network and timeout errors. It has `SqlState` too (null for errors that are not PostgreSQL's).
- Not converted yet in application code: connection strings in `appsettings*.json` and
  `web.config`/`app.config`; `switch` on `SqlException.Number`; property patterns
  (`ex is SqlException { Number: 2627 }`).
- WP A.3 departs from the plan in these places:
  - The application idioms are in a `CommandTranslator` beside `ScriptTranslator`, not a
    parameters mode inside it. Its command never becomes a DO block; procedural SQL is S2P8012.
  - Without `--source`, SCOPE_IDENTITY reads `lastval()` (S2P8011, Warning) rather than being
    left alone.
  - A bare SUM of an int column is cast to integer as well as COUNT. The rule is whatever SQL
    Server types as int.
- Not translated yet in SQL in code: multi-line SQL is written back without the original's
  indentation; SQL held in resources, configuration or another file.
- WP A.4 departs from the plan in these places:
  - `ParameterDirection.Output` stays Output. Npgsql leaves Output parameters out of a CALL and
    reads the INOUT value back from the row, and the converted INOUT parameter defaults to NULL
    (WP 3.4). InputOutput fails in Npgsql when the code sets no value ("Parameter cannot be
    null").
  - The parameter names the code passes are renamed to the converted procedure's (lower case).
    Npgsql uses named notation with the names quoted exactly as given. The plan said names keep
    their T-SQL spelling.
- Established with Npgsql 10: `CommandType.StoredProcedure` issues
  `CALL name("Param" => $1, …)`, with each parameter name quoted as written in the code.
- Established with Dapper.Contrib 2.0.78 and Npgsql 10: Contrib's PostgreSQL adapter quotes
  property names in INSERT, UPDATE and DELETE, but not in Get, GetAll or the key comparison. So
  with lower-case converted names, PascalCase entities fail on Insert ("column "Name" … does
  not exist"). Contrib has no column mapping and no public way to add an adapter, so this is
  reported (S2P8030) rather than fixed.
- WP A.6 departs from the plan in these places:
  - `ToTable`, `HasColumnName`, `[Table]` and `[Column]` names are not rewritten one by one.
    The naming loop at the end of OnModelCreating lower-cases every name the model ends up with,
    explicit or by convention, so the explicit names need no edit.
  - The loop also gives `DateTime` properties without a column type
    `timestamp without time zone`. Npgsql's EF provider maps DateTime to timestamptz by default,
    and writing an unspecified-kind DateTime there fails.
  - `EF.Functions` methods are reported rather than mapped.
- Established with EF Core 10 and Npgsql's provider 10.0.3: a model named in lower case through
  `SetTableName`/`SetColumnName` at the end of OnModelCreating, with DateTime columns typed
  `timestamp without time zone`, works against the schema sql2pg converts (query, insert with a
  database default, update, FromSqlRaw, delete).
- WP 4.1 departs from the plan in these places:
  - `DROP INDEX` keeps the statement's own `IF EXISTS` rather than always adding it, so a
    missing index fails on both engines alike.
  - `NOCHECK`/`CHECK CONSTRAINT` need no pair in one routine. NOCHECK is `DROP CONSTRAINT IF
    EXISTS`, and CHECK adds the constraint back `NOT VALID` only when it is missing. So the
    pair works when two procedures split it (load procedures do), and unpaired statements
    translate too.
  - `REBUILD` is `IF to_regclass(index) IS NULL THEN CREATE INDEX … ELSE REINDEX`, instead of
    tracking a DISABLE in the same routine.
  - Added from the corpus: `ALTER TABLE … ADD`/`DROP CONSTRAINT` (keys, foreign keys, checks,
    defaults) and `DROP PROCEDURE`/`DROP FUNCTION` → `DROP ROUTINE`. WideWorldImportersDW's
    `Configuration_PopulateLargeSaleTable` drops its keys and indexes this way.
  - Left as S2P5070: the temporal-table statements (`SET (SYSTEM_VERSIONING …)`, `ADD`/`DROP
    PERIOD`, WP 4.2), and `DROP TRIGGER` of triggers the model does not have (WideWorldImporters
    creates them with dynamic SQL, so their tables are not known).
- WP 4.2 departs from the plan in these places:
  - Two triggers rather than one: `<table>_period` (BEFORE INSERT OR UPDATE) sets the period, and
    `<table>_history` (AFTER UPDATE OR DELETE) copies old rows. SQL Server switches the two
    separately (`SYSTEM_VERSIONING` and the period), and WideWorldImporters' load procedures
    switch both. The history copy is AFTER, so a row another BEFORE trigger cancels leaves no
    history.
  - `SYSTEM_VERSIONING = OFF` disables only the history trigger, and `DROP PERIOD` the period
    trigger. The plan had one trigger disabled for both.
  - The period start is the transaction start in UTC (`now() AT TIME ZONE 'UTC'`), and the end
    of time is `…23:59:59.999999` at the column's precision, so PostgreSQL does not round it
    into the year 10000.
  - Not done: `HIDDEN` period columns (they show in `SELECT *`), the data consistency check, and
    a script's history table that SQL Server names itself (reported).
- Established with SQL Server 2022: a procedure is compiled whole before it runs. So
  `DROP PERIOD` and `ADD PERIOD` cannot share one procedure, and a static write to a
  GENERATED ALWAYS column fails at CREATE PROCEDURE. Load procedures split them, and write the
  period with dynamic SQL. `SYSTEM_VERSIONING = ON` with `DATA_CONSISTENCY_CHECK` rejects a
  period start earlier than the history's latest end.
- WP 4.3 departs from the plan in these places:
  - Everything goes through `XMLTABLE`, not `xpath()`. `value()` is a column of the target type,
    which gives the string value of the node (entities decoded, mixed content joined) and NULL
    when it is missing, as SQL Server does. A `nodes()` row is read from `'/*'` so relative paths
    start at the element. `xpath()` would read every path from the document node and return the
    node's markup rather than its value.
  - XML indexes stay S2P2003 (not created); no expression index is derived from the paths.
  - An absolute path under a `nodes()` row reads from that element as the root. This is correct
    for nodes of the root element (all of AdventureWorks), not for deeper ones.
- Established with SQL Server 2022 and PostgreSQL 18:
  - On untyped xml, SQL Server requires a static singleton in `value()`, so `(a)[1]/b` fails;
    with a schema collection, as AdventureWorks has, it passes.
  - PostgreSQL rejects `XMLNAMESPACES(DEFAULT …)`, and errors in XMLTABLE when a non-xml column
    path returns more than one node.
  - PostgreSQL converts a CAST of a literal while planning, so even a CASE branch that is not
    taken fails on a bad literal. Casting through text avoids it.
  - From text with a date style, SQL Server reads `yyyymmdd` always. For datetime and
    smalldatetime it reads `yyyy-a-b` in the style's month/day order (`2000-06-01` is 6 January
    under 103). For date and datetime2 it reads ISO 8601 with `T`, and rejects `yyyy-mm-dd`
    under styles 1, 101, 103, 104 and 110.
- WP 4.4 departs from the plan in these places:
  - The sort key is a `bigint[]`: each level's numbers, then a sentinel below any label. So array
    order is depth-first, and a dotted label (`1.1`) falls between `1` and `2` and after the
    subtree of `1`.
  - Not done: indexes on a hierarchyid column, `MIN`/`MAX`, and `ORDER BY` on an output alias
    still order the text (S2P1105 says so).
  - `GetDescendant` steps between the two children's labels (a's first number + 1 when there is
    room, otherwise one level deeper). All 15 grid pairs, dotted and negative ones included,
    match SQL Server 2022.
- Established with PostgreSQL 18: a VIRTUAL generated column may not call a user-defined
  function (an SQL function in mssql_compat included), so such a column is created STORED.
- WP 4.5 departs from the plan in these places:
  - Application code: `SqlDbType.Structured` is still reported (S2P8003), now with the call shape
    the converted routine needs (`MapComposite<T>` and a `T[]`). The plan's generated record
    class and rewritten `DataTable` code are not done: the code that fills a `DataTable` has no
    fixed shape to rewrite. The both-engines test shows the Npgsql call working against the
    converted procedure.
  - A script (not a routine) that declares a table-type variable and returns rows is still a DO
    block, which returns no rows (S2P5009).
  - Table-type keys, indexes, defaults and identity (SensorDataList's identity) are not kept in
    the composite type; callers supply every value.
- Established with PostgreSQL 18: a CALL argument may not be a subquery, so an array built
  with `ARRAY(SELECT …)` goes through a variable first.
- WP 4.6 departs from the plan in these places:
  - `JSON_MODIFY` is an mssql_compat helper over `json`, not `jsonb_set`. jsonb would reorder
    members (by length, then name) and reformat numbers; SQL Server keeps both, and appends a new
    member at the end.
  - `OPENJSON … WITH` is one `json_array_elements` with a `#>` path per column, not
    `jsonb_to_recordset`. A column of a scalar type is NULL when its path holds an object or
    array, as in SQL Server.
- Established with SQL Server 2022, for FOR JSON AUTO and JSON_MODIFY:
  - FOR JSON AUTO nests tables in the order their first column appears in the select list. A
    later column of an earlier table still goes to that table's level, and an expression joins
    the level of the column before it.
  - An unaliased table is keyed by its name as written (`dbo.City`).
  - The deepest level keeps every row.
  - A JSON_MODIFY result used directly as the new value goes in as JSON; the same text through a
    variable goes in as a string.
- WP 4.7 departs from the plan in these places:
  - The index is one GIN index with a `to_tsvector` expression per column, not one expression
    over the columns concatenated. SQL Server matches a search condition within one column
    (`CONTAINS(*, '"red" AND "fox"')` finds nothing when the words are in different columns), and
    a multicolumn GIN index serves a search of any subset of its columns.
  - The condition is parsed at run time by `mssql_compat.contains_query`, not at conversion time:
    AdventureWorks' `uspSearchCandidateResumes` passes it in a variable. The helper rewrites it in
    `to_tsquery`'s syntax and lets `to_tsquery` stem and drop stop words. It is IMMUTABLE, so a
    constant is folded while planning; a variable goes in a scalar subquery, worked out once.
  - FREETEXT is `freetext_query`: any of the stemmed words (`|`), not `websearch_to_tsquery`,
    which needs all of them.
  - `NEAR` with more than two terms checks each neighbouring pair; a distance over 100 is AND.
  - `SERVERPROPERTY('LCID')` is translated (as `default_text_search_config`'s LCID), ahead of WP
    4.10, because `uspSearchCandidateResumes` uses it.
  - Not done: full-text indexes on indexed views, `PROPERTY()` searches, `ALTER FULLTEXT INDEX`
    and `CREATE FULLTEXT INDEX` inside routines (S2P5070 as before), custom stoplists and the
    thesaurus. The corpus agreement queries do not cover full-text search: the corpus SQL Server
    is the stock image, without Full-Text Search.
- Established with SQL Server 2022 (with mssql-server-fts), for full-text search:
  - Multi-column CONTAINS needs every term in one column.
  - A prefix term works only in double quotes (`"moun*"`); `moun*` bare is the word itself.
  - A phrase is in order and adjacent; noise words in a phrase are skipped (`"the fox"` matches
    `fox`), but a noise word as an AND operand matches nothing.
  - `NEAR((a, b), n)` allows at most n words between, noise words counted, in either order unless
    TRUE. The generic `a NEAR b` / `a ~ b` matched at 21 words apart.
  - CONTAINS does not stem (`run` does not match `running`; FORMSOF(INFLECTIONAL, run) does);
    FREETEXT stems and matches any word.
  - `NOT` first, `OR NOT` and two bare words with no operator are syntax errors.
  - xml columns: element text and attribute values are indexed, tags are not. Hyphenated words
    are indexed whole only (`mail` does not match `e-mail`).
  - Ranks are integers from 0 to 1000; SERVERPROPERTY('LCID') is 1033 on the container.
  - The stock `mssql/server:2022` image has no Full-Text Search
    (`FULLTEXTSERVICEPROPERTY('IsFullTextInstalled')` = 0). The engine there is not a dpkg
    package, so installing `mssql-server-fts` brings `mssql-server` from Microsoft's repository
    along with it.
- Established with PostgreSQL 18:
  - An xml column cast to text is immutable and can be indexed; the parser skips tags, attribute
    values included. `xpath()` is not immutable.
  - `to_tsquery` drops stop words from phrases (`'the' <-> 'fox'` is `'fox'`) and from AND,
    keeping the distance (`fox <-> the <-> dog` is `fox <2> dog`). Stop words count as positions in
    `to_tsvector`, as noise words do in SQL Server's NEAR.
  - A function with `SET client_min_messages` is still folded while planning when its arguments
    are constants.
- WP 4.8 departs from the plan in these places:
  - More methods than the plan lists are mapped (the geometry set operations, envelope, centroid,
    convex hull, point accessors, `STGeometryType`, `STNumPoints` and others), each checked on both
    engines. Geography forms PostGIS has only for geometry stay S2P3030, as their results would be
    planar arithmetic on degrees.
  - Geography `STContains`/`STWithin` are `ST_Covers`/`ST_CoveredBy` (PostGIS has no geography
    ST_Contains); they differ on the boundary (S2P3031).
  - The PostGIS data copy had three faults that only a real server showed, now fixed: values read
    out of column order under sequential access, SRID 0 written with the SRID flag, and the
    verifier reading PostGIS types through Npgsql (now `ST_AsEWKB`). The catalog reader also gave
    spatial and xml indexes no columns.
  - Not done: the corpus agreement queries do not run with postgis (the harness uses the shared
    stock PostgreSQL); `MakeValid`/`Reduce`/set operations on geography; CAST of a spatial value
    to a string.
- Established with SQL Server 2022 and PostGIS 3.6 on PostgreSQL 18, for spatial values:
  - Geography distances, lengths and areas agree to about 1e-7 relative (polygon area 5e-7): both
    compute on the WGS 84 ellipsoid. `ST_Distance(…, false)` (the sphere) is 2.5e-4 off.
  - SQL Server writes WKT as `POINT (x y)` with `, ` between coordinates, drops Z and M in
    STAsText, prints up to 17 significant digits and `1E-07`; PostGIS writes `POINT(x y)`, keeps Z
    (`POINT Z (…)`), prints 15 significant digits and `0.0000001`.
  - STLength of a polygon is its perimeter (ST_Length gives 0); STCentroid is NULL for points
    and lines; STEnvelope's ring is counter-clockwise from the lower-left corner, ST_Envelope's
    clockwise.
  - STBuffer around a point is a 129-point circle, which is `ST_Buffer(…, 'quad_segs=32')` to
    1e-14 in area. Buffers of lines differ by about 2e-5, geography buffers by about 7e-4.
  - STGeometryType names are ST_GeometryType's without `ST_` (`Polygon`, `LineString`).
  - SQL Server keeps a geography longitude one ulp past 180 (WideWorldImporters' borders of
    countries 73 and 177 have 180.00000000000003); PostGIS geography stores it as 180. The copier
    writes such values (within 1e-10 of ±180/±90) as the limit, so they verify.
  - PostGIS stores SRID 0 without the EWKB SRID flag; `ST_AsEWKB` has no geography form.
  - SQL Server returns NULL for a method on values with different SRIDs; PostGIS raises an error.
- WP 4.9 departs from the plan in these places:
  - The padding is `rpad(x, n)`, not `rpad(x::text, n)` (rpad takes text, and bpchar converts).
    Comparisons drop it at the end of an operand, and through UPPER, LOWER, LTRIM, LEFT and
    SUBSTRING, whose results end where their input does. Where padding stays inside an operand
    (RIGHT, REPLACE, REVERSE), both sides are compared with rtrim, as SQL Server compares.
  - Not done: a char value assigned to a varchar variable, parameter or column, a UNION of char
    and varchar, and ISNULL(char(3), 'longer') (SQL Server truncates to the first argument's
    length) still lose or keep the wrong padding. Non-Unicode LIKE on varchar values with trailing
    spaces is not changed either.
- Established with SQL Server 2022, for char values:
  - A char(n) value keeps its trailing spaces wherever it becomes a string: CASE/IIF/COALESCE with
    a varchar branch, `+`, CAST/CONVERT to varchar, UPPER, LEFT, RIGHT, SUBSTRING, REPLACE, LTRIM,
    CONCAT, STRING_AGG, assignment to a varchar variable, UNION with varchar. `=` and LEN ignore
    them; DATALENGTH counts them.
  - Strings of different types combine to the Unicode type if any is Unicode, the varying type if
    any is varying, and the longest length (nchar(5) with varchar(10) is nvarchar(10)).
  - Non-Unicode LIKE lets the value's trailing spaces be left off, any number of them, for char
    and varchar ('ab    ' matches 'ab', 'ab_', '______', not '_______'). Unicode LIKE (nchar,
    nvarchar, or an N'' pattern) counts them, as PostgreSQL's LIKE on bpchar does. A pattern's own
    trailing spaces always count.
  - SQL_VARIANT_PROPERTY of `CASE WHEN 1 = 1 …` reports the folded branch's type, not the CASE's.
- Established with PostgreSQL 18: bpchar keeps its padding on output, in LIKE and in concat(),
  and drops it when it becomes text (`||`, casts, string functions).
- WP 4.10 departs from the plan in these places:
  - Catalog views are not mapped to pg_catalog joins one query at a time. They become views in
    mssql_compat over a table the converter fills with every object's SQL Server and PostgreSQL
    names (and a column's SQL Server type), so the views give SQL Server's names and code that
    compares names, from literals or variables, keeps working without a name mapping in the query.
  - `sp_rename`, `sp_help`, `sp_columns` and `sp_tables` are not translated (S2P5023 as before).
  - Not answered (S2P3043): sys.indexes, sys.index_columns, sys.foreign_keys, sys.types,
    sys.database_principals and the other catalog views; SERVERPROPERTY properties beyond those
    documented under S2P3044. Objects created after the conversion are not in the name table.
- Established with SQL Server 2022 and PostgreSQL 18, for the catalog:
  - A sequence that has never handed out a value reports its start value as current_value;
    pg_sequences.last_value is NULL then.
  - IS_ROLEMEMBER('db_owner') is 1 for the database owner (sa through dbo); pg_has_role on the
    database owner's oid gives the same for the PostgreSQL owner.
- WP 4.11 departs from the plan in these places:
  - `appSettings` values in web.config/app.config that are SQL Server connection strings are
    converted too, as JSON values outside `ConnectionStrings` are.
  - No both-engines test: the conversion is the one WP A.2's both-engines runs already exercise;
    the new part is finding the values in the files, which the golden files cover.
- WP 4.12 departs from the plan in these places:
  - The settings are session settings (`set_config(…, false)`), not transaction-local ones, and are
    published again after each COMMIT or ROLLBACK in the CATCH block. Established with PostgreSQL
    18: a ROLLBACK undoes a session-level set_config made in the rolled-back transaction too, and
    AdventureWorks' pattern rolls back before it calls uspLogError.
  - Only CATCH blocks that call code (EXEC, dynamic SQL, a function of the database) publish. A
    block that leaves by RETURN or by raising again leaves its error published.
- Established with SQL Server 2022: ERROR_PROCEDURE() in a procedure called from a CATCH block is
  the schema-qualified name of the procedure whose error is handled (dbo.Work), and ERROR_*() is
  NULL there again once the CATCH block has finished.
- WP 4.13 departs from the plan in these places:
  - The key is the row's ctid, not its primary key, so tables without one work too. ctid is
    unique per table and stable within a statement (a partitioned table repeats it across
    partitions; such a target is not handled specially).
  - An UPDATE whose target is in an outer join and whose SET reads the joined tables stays
    S2P4008 (Error).
  - PIVOT/UNPIVOT forms the translator does not take yet are unchanged: none were left in the
    corpus or the BACKLOG notes to measure against.
  - FOR XML AUTO over joins (nested elements), EXPLICIT and PATH names with `/` are not done.
- Established with SQL Server 2022, for FOR XML: `PATH` with no name is `PATH('row')`; NULL
  columns are left out in RAW, AUTO and PATH (as PostgreSQL's xmlattributes and xmlforest do); a
  bit is written 1 or 0; a FOR XML query with no rows returns NULL, also with ROOT.
- WP 4.14 departs from the plan in these places:
  - char columns are not indexed: their LIKE compares the column as text (WP 4.9), which an index
    on the column does not serve.
  - Only a literal pattern counts as a leading-wildcard search; `'%' + @x` built at run time does
    not (name the column with --trigram-index).
- Established with PostgreSQL 18: on a small table (20,000 short rows) the planner may scan the
  table for LIKE '%smith%' although the trigram index can serve it; with enable_seqscan off it
  uses a bitmap scan of the index.
- WP 4.15 departs from the plan in these places:
  - A job's procedure is synthesised as T-SQL (`CREATE PROCEDURE sql2pg_agent.[job] AS BEGIN …
    END`, a step that goes on after failing in TRY/CATCH) and converted like any other
    procedure, so step commands get the same translation and diagnostics.
  - "Go to step n" is not kept (S2P2024): the steps run in order.
  - Schedules are cron in UTC (or cron.timezone); SQL Server Agent ran them in the server's local
    time. Active start and end dates are not kept.
  - pg_cron installs and runs only in cron.database_name, so the target database must be that one.
- Established with SQL Server 2022 and PostgreSQL 18:
  - msdb's job tables can be filled with sp_add_job, sp_add_jobstep, sp_add_schedule and
    sp_attach_schedule while SQL Server Agent is not running, as in the container.
  - The PGDG package postgresql-18-cron installs into the postgres:18 image; the server needs
    shared_preload_libraries=pg_cron and cron.database_name at start. A build of that image once
    failed on an apt download and passed on the rerun; the test is skipped when it cannot be built.
- WP 4.16 departs from the plan in these places:
  - The rowversion mark is the database's @@DBTS before the pass, not the largest rowversion
    copied: a row changed while the pass runs is taken again next time, never missed.
  - Big tables are not split into key ranges for the incremental passes (the first pass is).
- Established with SQL Server 2022: change tracking reports a row inserted and then updated as
  one insert and a row deleted after a change as one delete, so the net change per key is what
  the sync applies; `ALTER DATABASE CURRENT SET CHANGE_TRACKING = ON` works from a user database.
- WP 4.17 departs from the plan in these places:
  - The plan's list (virtual generated columns, uuidv7, MERGE RETURNING, OLD/NEW in RETURNING,
    LIKE on nondeterministic collations) missed one 17-only form the shop tests use:
    `WHEN NOT MATCHED BY SOURCE`. Before 17 it runs as data-modifying CTEs (S2P4016).
  - `MERGE … OUTPUT` on 15 and 16 is reported (S2P4015), not rewritten: rebuilding RETURNING from
    separate UPDATE/INSERT/DELETE CTEs would change which WHEN clause takes a row.
  - The old row of an UPDATE's OUTPUT before 18 is a whole-row column of a self-joined copy,
    `(sql2pg_old.sql2pg_row).col`, so the copy hides none of the statement's unqualified columns.
  - `--target-version` was added to the CLI (it was a config-file setting only).
- Established with PostgreSQL 15, 17 and 18 (scratch containers):
  - With a nondeterministic ICU collation, 15–17 reject `LIKE` and the substring searches
    (`strpos`, `position`, `replace`, `split_part`, `string_to_array`): "nondeterministic
    collations are not supported for LIKE / substring searches". 18 accepts both. No version
    accepts `SIMILAR TO` or `~` ("… for regular expressions"), so `[a-c]` patterns on such a
    column failed on 18 too until this WP.
  - `lower(x COLLATE "default") LIKE lower(p)` and `… SIMILAR TO lower(p)` work on all four.
  - A data-modifying CTE on `MERGE` (`WITH d AS (DELETE …) MERGE …`) works on 15.
  - The official postgres images set `ENV PG_VERSION=16.16-1.pgdg13+1` (and `PG_MAJOR=16`). In
    the classic builder (Testcontainers, `DOCKER_BUILDKIT=0`) that overrides a Dockerfile
    `ARG PG_VERSION` after `FROM`, so `postgresql-${PG_VERSION}-cron` was not found; BuildKit
    builds had hidden it. The Agent-job test had been skipped wherever the image was not cached.
  - MERGE's `merge_action()` and `RETURNING` exist from 17; on 17 a deleted row's `RETURNING t.col`
    is the old value and an inserted row's the new one.
- WP 4.18 departs from the plan (written into it with this WP) in these places:
  - Dynamic `CREATE TABLE`, `CREATE TYPE … AS TABLE`, `DROP TYPE`, `ALTER DATABASE` and the
    in-memory OLTP forms stay S2P5060: in the corpus they occur only in
    `Configuration_EnableInMemory`, which also needs the server's data path and filegroups.
  - Security policies are converted only where procedures create them; the catalog reader does not
    read `sys.security_policies`.
  - `SESSION_CONTEXT`, `STRING_ESCAPE(…, 'json')` and `CONVERT` styles 6/7/106/107 were not in the
    WP's list; they blocked procedures that dynamic SQL creates, so they are in.
  - `DataLoadSimulation.Configuration_ApplyDataLoadSimulationProcedures` stays Manual: two of the
    21 routines it creates need `COMPRESS` (no core equivalent) and, without PostGIS, a spatial
    property.
- Found and fixed on the way:
  - A long `+` chain (60 pieces) is a deep tree; folding it recursively hit the depth guard, so
    the pieces are folded one by one.
  - Following dynamic SQL text back to its SET ignored assignments later in an enclosing `WHILE`.
  - Translated dynamic SQL with several statements was joined without semicolons, and dynamic SQL
    was always written for PostgreSQL 18 (WP 4.17 missed the writer's hard-coded version).
- Established with SQL Server 2022 and PostgreSQL 15–18:
  - SQL Server's row-level security filters `sa` too; PostgreSQL's does not apply to a superuser
    even with `FORCE ROW LEVEL SECURITY` (the test reads as an ordinary login role).
  - `to_json(text)::text` escapes as `STRING_ESCAPE(…, 'json')` does except for `/`, which only
    SQL Server writes as `\/` (tab, newline, backslash, quote, `NCHAR(1)` agree).
  - `to_char(…, 'Mon DD, YYYY')` and `'DD Mon YYYY'` give styles 107 and 106 under `us_english`.
- WP 4.19 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - Audits, partitioning and in-memory were to keep their errors with a specific message. Most of
    those procedures test `SERVERPROPERTY(N'IsXTPSupported') = 0` first, which PostgreSQL answers
    with 0, so the branch that never runs is now a comment (S2P3046) and they convert; only the
    audit procedures, which query `sys.server_audits` outside any such check, keep their errors.
  - `sys.indexes` gets its own table, `mssql_compat.index_names`, rather than more kinds in
    `object_names`: an index has a table, a type and flags of its own.
  - `Configuration_ApplyFullTextIndexing` stays Manual: besides `sys.fulltext_*` it re-creates five
    search procedures the database already has, with full-text bodies, and the semantic model
    keeps one definition per name.
- Established with SQL Server 2022 and PostgreSQL 15–18:
  - The role procedures (create a role and add a member, each twice; remove the member; drop the
    role twice) give the same
    principals, memberships and messages on both engines, with a role name in mixed case with a
    space in it.
  - In the SQL Server 2022 test container `IsHadrEnabled` is 0 and `EngineEdition` is not 5, as
    the converter answers, so the both-engines test of feature checks uses those two.
  - PostgreSQL 16 lets a role be granted by more than one grantor, one `pg_auth_members` row
    each; `sys.database_role_members` lists each pair once.
- WP 4.20 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - PostGIS on 19 was to be skipped until postgis/postgis has a 19 image. The tests build one
    instead (`Infrastructure/postgres-postgis/Dockerfile`, from PGDG's `postgresql-19-postgis-3`),
    so the spatial tests and the PostGIS corpus scans run on 19 too. When postgis/postgis
    publishes a 19 image, `Engines.PostGisImage` can use it.
  - Two fixes apply to every version, not only 19: `IGNORE NULLS`, which the converter dropped
    without a diagnostic, and included columns too wide for a btree row (S2P2026), which 19's lz4
    compression brought out.
- Established with SQL Server 2022 and PostgreSQL 15–19:
  - PostgreSQL 19's new keywords (`ignore`, `lsn`, `repack`, `respect`, `wait`) are unreserved,
    and 15–17 reserve nothing that 18 does not (`pg_get_keywords()` on each).
  - `LAG`/`LEAD(x, n, d) IGNORE NULLS`: SQL Server goes to the row n away, gives `d` only when
    that row is past the partition, and otherwise the first non-NULL value from there on, or
    NULL. PostgreSQL 19 counts n non-NULL rows and gives `d` when there are fewer (the
    standard's rule). They agree for `LAG`/`LEAD(x) IGNORE NULLS` and for `FIRST_VALUE`/
    `LAST_VALUE … IGNORE NULLS`. SQL Server 2022 accepts `IGNORE NULLS` on all four.
  - A 3800-character random ASCII value in an `INCLUDE` column fails the INSERT on PostgreSQL 18
    ("index row size 3816 exceeds btree version 4 maximum 2704"); SQL Server takes it.
  - PostgreSQL 19's `default_toast_compression` is lz4: AdventureWorks' `ProductReview.Comments`
    rows that pglz fit into `IX_ProductReview_ProductID_Name` on 18 do not fit on 19.
  - postgis/postgis has no 19 image (2026-11-20); PGDG has `postgresql-19-postgis-3` (3.7.0)
    and `postgresql-19-cron` (1.6.8).
- WP 4.21 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - The candidates it named (`JSON_OBJECT`, `JSON_ARRAY`, `JSON_ARRAYAGG`'s properties) were not
    silent: the functions are reported (S2P3001). The drops were elsewhere: named windows, `TRIM`
    options, `TABLESAMPLE`, `WITH XMLNAMESPACES`, `EXEC … AS USER`, `ADD … DEFAULT` without
    `WITH VALUES`, HIDDEN / masked / encrypted columns (which the catalog reader never read), and
    `SET` in dynamic SQL.
  - Not covered, and on the reviewed list as such: `HISTORY_RETENTION_PERIOD` (PostgreSQL keeps
    the history rows) and typed `xml` (no schema validation).
- Established with SQL Server 2022 (compatibility 160) and PostgreSQL 15–19:
  - `WINDOW w AS (…)` with `OVER w` and `OVER (w ORDER BY …)`, and a window built on another
    (`running AS (byid ROWS …)`), give the same rows on both engines.
  - `TRIM(LEADING | TRAILING | BOTH chars FROM s)` agrees with `ltrim` / `rtrim` / `btrim`.
    ScriptDom reads `TRIM(LEADING FROM s)` as trimming the characters of a column named
    `leading` (reported as an unresolved column).
  - `TABLESAMPLE (100 PERCENT)` reads every row on both.
  - `ALTER TABLE … ADD c int NULL DEFAULT 0` leaves the existing rows NULL in SQL Server; with
    `WITH VALUES` they get the default; PostgreSQL's `ADD COLUMN … DEFAULT` always fills them.
  - `sys.masked_columns.masking_function` is the function as written (`email()`); the period
    columns of `GENERATED ALWAYS AS ROW START HIDDEN` have `is_hidden = 1`.
  - The TSql160 parser rejects SQL Server 2025's `TOP … WITH APPROXIMATE`, `FETCH … WITH
    APPROXIMATE`, `ORDER BY ALL` and `JSON_QUERY … WITH ARRAY WRAPPER`, and the removed `COMPUTE`.
- WP 4.22 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - `TRY_CONVERT` 126 was not blocked by the computed column's immutability: `TRY_CONVERT` with
    any style was untranslated, and WideWorldImporters writes the style in parentheses
    (`(126)`), which `CONVERT` did not accept either.
  - The ICU work went beyond `REPLACE` and friends before 18: `CHARINDEX` through its helper was
    case-sensitive on 18 as well, and `ISNUMERIC` (a regular expression) failed on every version.
- Established with SQL Server 2022 and PostgreSQL 15–19:
  - `TRY_CONVERT(datetime2, s, 126)` reads `yyyy-mm-dd` or `yyyy/mm/dd`, then an upper-case `T`
    or a space and `hh:mi:ss` with a fraction of any length (rounded to the type), then `Z` or
    an offset up to ±14:00, which it ignores; also `yyyymmdd` and `Jan 2 2013`. A blank string
    is 1900-01-01. NULL for `2013-01-02T07:05`, a lower-case `t`, padding, `24:00:00`,
    `07:05:60`, a space before the offset and `+15:00`. `TRY_CONVERT(datetime, s, 126)` reads
    fewer (no seven-digit fraction, no space, no offset) and allows padding.
  - On a nondeterministic ICU collation, PostgreSQL 15–17 refuse `strpos`, `position`,
    `replace`, `split_part`, `starts_with`, LIKE and regular expressions; 18 allows all but
    `starts_with` and regular expressions. A SQL function's parameter takes the call's
    collation, so a regular expression inside a helper fails too (on 18 as well).
  - `sys.tables.history_retention_period` is -1 for INFINITE; `sys.columns.xml_collection_id`
    is 0 for untyped xml, and `is_xml_document` says DOCUMENT.
  - The catalog reader now needs SQL Server 2017 or later (`history_retention_period`).
- WP 4.23 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - INSTEAD OF on a table was to keep statement-level semantics with a BEFORE ROW trigger that
    collects rows and an AFTER STATEMENT trigger that runs the body. It runs the body per row
    instead, as INSTEAD OF on views already did (S2P6003 says so for a body of more than one
    statement): AdventureWorks' two triggers behave the same per row, and a statement-level
    emulation would need a temporary table per statement.
  - Two bugs came up on the way and are fixed for every trigger and routine: `ROLLBACK` in a
    trigger raised at once (inside TRY it ran the CATCH, and dVendor's would have ended without
    an error), and an IF or WHILE condition starting with a CASE did not compile.
- Established with SQL Server 2022 and PostgreSQL 15–19:
  - An INSTEAD OF DELETE trigger on a table fires once per statement, also for a statement that
    matches no rows; `@@ROWCOUNT` in it is the statement's row count. A statement the trigger
    runs on its own table does not fire it again (an INSTEAD OF INSERT that inserts the rows
    rewritten inserts them).
  - `ROLLBACK` in a trigger does not stop the trigger: TRY ... CATCH around it does not reach the
    CATCH, and the batch ends with error 3609 afterwards.
  - PL/pgSQL reads an IF condition up to the first THEN outside parentheses: `IF CASE WHEN x THEN
    0 ELSE 1 END > 0 THEN` is a syntax error ("syntax error at end of input"), which
    `check_function_bodies = off` hides until the routine runs.
- WP 4.24 departs from the recommendation it started from (NEXT_SESSION, written into the plan
  with this WP) in these places:
  - The helper does not walk the document with `xpath` and rebuild it: `xpath` finds the target,
    `count(preceding::*) + count(ancestor::*)` (or `preceding::text()`) says which node it is,
    and the document's text, split into tags and text by one regular expression, is changed at
    that node. So any XPath 1.0 path the WP 4.3 translation gives works as a target, and the
    rest of the document keeps its text.
  - Beyond the two forms AdventureWorks uses, `insert … before/after`, attributes and predicates
    are translated too; `delete` and decimal `div` stay reported.
  - Typed xml needed the element types, so the catalog and script readers now keep each XML
    schema collection's schema documents.
  - Two bugs came up in the both-engines run and are fixed for every trigger (see Done).
- Established with SQL Server 2022 and PostgreSQL 15–19:
  - `modify()` on a NULL document fails (error 5302), also in an UPDATE where only some rows are
    NULL; a target that is not found leaves the document as it is; replacing a value with an
    empty sequence (a NULL `sql:variable`, `data()` of a missing node, text that is not a number
    in arithmetic) fails (6325).
  - `insert … into` without `as first` or `as last` inserts as the last child. Inserting an
    element whose namespace differs from the one in scope declares it (`xmlns=""`, or a prefix
    `p1` where SQL Server can). A constructor loses its whitespace-only text between tags, its
    CDATA becomes text, and `<z></z>` is written `<z/>`; replacing a text node with `""` leaves
    `<t/>`.
  - In arithmetic, a node of untyped xml is xs:double; a node of typed xml has its schema type
    (xs:decimal + xs:decimal is xs:decimal, written without trailing zeros: `8249`, `-10.123456`).
    `int` columns and variables are xs:integer, `decimal`/`numeric`/`money` xs:decimal, `float`
    xs:double, `bit` xs:boolean (`true`). An xs:double is written with 15 significant digits, in
    decimal notation from 1E-6 to below 1E6, otherwise as `3.0E6`, `9.0E-7`; zero is `0.0E0`.
    Decimal `div` gives SQL Server's own scale (`1 div 3` is `0.333333`). Typed xml stores
    values normalised: `<T>0.00</T>` reads back `<T>0</T>`, `007` as `7`.
  - Text is escaped `&lt; &amp; &gt;` and carriage return `&#x0D;`; attributes also `&quot;`,
    tab `&#x09;` and line feed `&#x0A;`. Untyped xml loses whitespace-only text nodes when SQL
    Server stores it.
  - PostgreSQL keeps whitespace-only text nodes in `xpath` (libxml2's default) and counts a
    CDATA section as a text node of its own; `namespace::*[name()='']` gives the default
    namespace in scope. On 18 and 19 a VIRTUAL generated column is NULL in a trigger's transition
    tables and in `NEW` of an AFTER row trigger.
