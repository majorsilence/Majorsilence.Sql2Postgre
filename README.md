# Majorsilence.Sql2Postgre

A .NET library and `dotnet tool` for migrating Microsoft SQL Server databases to PostgreSQL:
schema, data, and T-SQL code (views, functions, stored procedures, triggers and ad-hoc SQL)
converted to PostgreSQL SQL and PL/pgSQL, then verified against a real PostgreSQL.

Status: planning. [ROADMAP.md](ROADMAP.md) explains the design;
[docs/IMPLEMENTATION_PLAN.md](docs/IMPLEMENTATION_PLAN.md) is the step-by-step build plan.

The output is stock PostgreSQL: core features plus a short list of permitted extensions
(PostGIS, pgvector, pgcrypto, pg_trgm, pg_cron, hstore, uuid-ossp, pg_stat_statements).

## License

Tri-licensed under your choice of MIT, Apache 2.0, or BSD 3-Clause.
See [LICENSE](LICENSE) for the full text of all three licenses.
