# Backlog

What was done and measured, as it lands. The plan is in
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md).

## Done

- WP 0.1 — solution skeleton.
- WP 0.3 — golden runner, container fixtures, `BothEngines`, `CanonicalValue`.

## Measured

Nothing yet.

## Notes for later

- A machine with only the .NET 10 SDK builds `net10.0` alone; `Directory.Build.props` adds
  `net11.0` when the SDK is 11 or later. CI installs both SDKs.
- Package versions are central (`Directory.Packages.props`).
- `CanonicalValue` lives in `src/…Data` rather than the integration tests (the plan's WP 0.3 placement), because WP 1.11's verifier must format values exactly the same way. Its tests are unit tests.
- First container run pulls the SQL Server 2022 image (~10 minutes on a cold machine); later runs start in seconds.
