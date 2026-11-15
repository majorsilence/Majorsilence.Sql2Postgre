# Changelog

## Unreleased

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
