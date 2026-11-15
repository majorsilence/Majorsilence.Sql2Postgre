# Changelog

## Unreleased

- **CI builds and tests on Ubuntu and Windows, runs the container tests against PostgreSQL 15,
  16, 17 and 18, and drafts a release with the packages on a `v*` tag.** Publishing that
  release pushes to nuget.org with Trusted Publishing. Before this there was no CI. (WP 0.2)
- **The solution builds and packs: seven packages (Model, SqlServer, Translation, Postgres,
  Data, Compat and the `sql2pg` tool) and three test projects.** Before this the repository held
  only the plan. (WP 0.1)
- **Golden-file tests and both-engines tests have a harness: a T-SQL case and its expected
  PostgreSQL output under `tests/*.Tests/Golden`, and one shared SQL Server and PostgreSQL
  container that run a query each and compare canonical results.** Before this there was no way
  to state a translation or an engine's behaviour as a test. (WP 0.3)
