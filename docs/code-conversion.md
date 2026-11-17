# Converting application code

`sql2pg code` converts C# and VB.NET code that uses SQL Server, through ADO.NET, Dapper,
Dapper.Contrib or Entity Framework Core, so that it uses PostgreSQL through Npgsql. It changes
what has to change. Anything it cannot change gets an `S2P8xxx` diagnostic (see
[diagnostics.md](diagnostics.md)).

```
sql2pg code <path> --out <folder> [--source <database or scripts>] [--config sql2pg.json] [--json report.json]
sql2pg code <path> --in-place ...
```

- `<path>` is a `.cs` or `.vb` file, a folder (searched recursively, skipping `bin/` and `obj/`), a
  `.csproj`/`.vbproj` (the files under its folder), or a `.sln`/`.slnx` (its projects).
- `--out` writes converted copies, mirroring the input tree. `--in-place` rewrites the files
  where they are. Keep them under version control, so the change can be reviewed as a diff.
- `--source` is the same as for `sql2pg schema`. With it, the converter knows the tables, identity
  columns and the PostgreSQL form of each stored procedure. Without it, rewrites that need that
  knowledge are reported instead of made.
- `--config` gives the conversion options the database is converted with. Name style, schema
  map and type overrides must match, so that names in the code match the converted schema.

## How it works

Each file is parsed with Roslyn, the C# or VB.NET compiler platform. Rules find what to change
and record edits: a span of the original text and its replacement. The edits are applied to the
original text, so formatting, comments, line endings and the file's encoding are kept. A file no
rule touches is written back byte for byte. Two edits that overlap are a converter bug, so the
file is left as it was with S2P9001.

The exit code is 0 when no file needs a person, and 2 when any file has an Error diagnostic.

## What is converted

The rules are added work package by work package (Stage A in the
[implementation plan](IMPLEMENTATION_PLAN.md)):

| Area | Work package |
|---|---|
| Edit engine, input discovery, `sql2pg code` | A.1 (done) |
| ADO.NET types, `SqlDbType`, exceptions, connection strings | A.2 (done) |
| SQL strings in code (ADO.NET, Dapper, EF Core raw SQL) | A.3 |
| Stored procedure calls | A.4 |
| Dapper and Dapper.Contrib | A.5 |
| Entity Framework Core | A.6 |

## ADO.NET

In a file that imports `Microsoft.Data.SqlClient` or `System.Data.SqlClient`, or names their
types in full:

- **Namespaces and types.** The import becomes `Npgsql`, plus `NpgsqlTypes` when `NpgsqlDbType`
  is used. These types are renamed:

  | SqlClient | Npgsql |
  |---|---|
  | `SqlConnection` | `NpgsqlConnection` |
  | `SqlCommand` | `NpgsqlCommand` |
  | `SqlDataReader` | `NpgsqlDataReader` |
  | `SqlParameter` | `NpgsqlParameter` |
  | `SqlParameterCollection` | `NpgsqlParameterCollection` |
  | `SqlTransaction` | `NpgsqlTransaction` |
  | `SqlDataAdapter` | `NpgsqlDataAdapter` |
  | `SqlCommandBuilder` | `NpgsqlCommandBuilder` |
  | `SqlConnectionStringBuilder` | `NpgsqlConnectionStringBuilder` |
  | `SqlClientFactory` | `NpgsqlFactory` |
  | `SqlBatch` | `NpgsqlBatch` |
  | `SqlBatchCommand` | `NpgsqlBatchCommand` |
  | `SqlException` | `NpgsqlException` (S2P8001) |

  Fully qualified names (`Microsoft.Data.SqlClient.SqlConnection`) are renamed too.
- **SqlDbType.** `SqlDbType.X` becomes `NpgsqlDbType.Y` for the PostgreSQL type the schema
  converter gives that SQL Server type. For example, `NVarChar` → `Varchar`, `Int` → `Integer`,
  `Bit` → `Boolean`, `DateTime2` → `Timestamp`, `DateTimeOffset` → `TimestampTz`,
  `UniqueIdentifier` → `Uuid`, `Decimal`/`Money` → `Numeric` and `VarBinary` → `Bytea`. The
  `SqlDbType` property becomes `NpgsqlDbType`. `DbType` is provider-neutral and stays as it is.
- **Errors.** `SqlException` becomes `NpgsqlException`. Comparisons of `.Number` with a known
  SQL Server error number, on a caught exception, parameter or local, become SQLSTATE
  comparisons: `ex.Number == 2627` → `ex.SqlState == "23505"`.
- **Connection strings.** A string literal that is a SQL Server connection string (a server
  key, and a database or credential key) is rewritten for Npgsql:
  - `Server`/`Data Source` → `Host` and `Port` (from `host,port`);
  - `Initial Catalog` → `Database`;
  - `User ID` → `Username`;
  - `Encrypt=True` → `SSL Mode=Require`;
  - timeouts and pool sizes are kept;
  - Windows authentication (S2P8005) and SQL Server-only keys (S2P8006) are dropped and
    reported.

  Connection strings in `appsettings.json` and `web.config` are not converted yet.
- **Reported:** `SqlBulkCopy` (S2P8004), table-valued parameters (S2P8003), `SqlTypes` (S2P8007),
  and SqlClient-only features (S2P8008).
