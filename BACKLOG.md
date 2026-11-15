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
