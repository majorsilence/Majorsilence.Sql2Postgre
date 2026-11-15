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

## Measured

Nothing yet.

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
