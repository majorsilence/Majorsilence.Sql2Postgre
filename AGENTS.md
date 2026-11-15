# Notes for AI agents

Read [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) before changing anything: it
sets out the work packages, the shared contracts and the definition of done.

- **Clean room.** Never read, copy or port other SQL Server → PostgreSQL converters,
  transpilers, loaders or compatibility layers (source, tests, mapping tables, docs). Sources of
  truth are the T-SQL reference, the PostgreSQL manual, and both engines run in tests.
- **Stock PostgreSQL.** Output uses core PostgreSQL plus only the permitted extensions listed in
  the plan's hard rule 2; any other extension needs the maintainer's approval first.
- **Nothing dropped silently.** Untranslatable constructs get a diagnostic code and stay in the
  output as a comment.
- Run the test projects separately (`tests/*.Tests`, `*.Integration.Tests`, `*.Corpus.Tests`).
- Every change gets a CHANGELOG entry under `## Unreleased`.
