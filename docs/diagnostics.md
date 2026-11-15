# Diagnostics

Every diagnostic the converter reports has a stable code: `S2P` and four digits. A code is never
reused for a different meaning. Severity decides an object's status: any **Error** makes it
`Manual` (a person must finish it), any **Warning** makes it `ConvertedWithWarnings`, and
**Info** changes nothing.

| Range | Area |
|---|---|
| S2P0xxx | Input / reading / parsing |
| S2P1xxx | Types, names, collations |
| S2P2xxx | Schema objects (constraints, indexes, sequences) |
| S2P3xxx | Expressions and built-in functions |
| S2P4xxx | Queries and DML |
| S2P5xxx | Procedural code |
| S2P6xxx | Triggers |
| S2P7xxx | Data copy and verification |
| S2P9xxx | Internal errors (a bug: the converter threw) |

## S2P0xxx — input, reading, parsing

### S2P0001 ParseError (Error)

A batch of a T-SQL script did not parse. The message gives the file, line and the parser's
complaint. The batch is skipped and reading continues with the next one, so objects it created
are missing from the conversion. Fix the script (often a missing `GO` between two `CREATE`
statements that must each start a batch) and read it again.

### S2P0101 EncryptedModule (Error)

The view, function, procedure or trigger was created `WITH ENCRYPTION`, so SQL Server does not
reveal its text and there is nothing to convert. It is listed so it is not forgotten. Supply its
original `CREATE` script (for example through the script reader) to convert it.

### S2P0102 ClrObject (Error)

The object is a CLR function, procedure, aggregate or trigger: compiled .NET code, not T-SQL.
PostgreSQL cannot run it; reimplement it in SQL or PL/pgSQL by hand.

## S2P1xxx — types, names, collations

### S2P1001 IdentifierTruncated (Warning)

PostgreSQL identifiers are at most 63 bytes (UTF-8); longer ones are silently cut by the
server, which can make two names equal. sql2pg shortens the name itself to its first 54 bytes,
`_`, and 8 hex digits of a hash of the whole name, so different long names stay different. Code
that refers to the object by its old name must use the new one; consider renaming it to
something shorter with a name map.

### S2P1002 IdentifierCollision (Warning)

Two different SQL Server names become the same PostgreSQL name (for example `OrderID` and
`Order_ID` under snake_case, or a table and an index that share a name: PostgreSQL keeps tables,
views, sequences, indexes and types in one namespace per schema). The later one gets `_2`, `_3`, …
Check that code referring to it uses the new name.

### S2P1101 FractionalSecondsReduced (Info)

`datetime2(7)`, `time(7)` and `datetimeoffset(7)` keep 100-nanosecond precision; PostgreSQL
keeps microseconds. The column becomes `(6)` and copied values are rounded to the microsecond.

### S2P1102 OffsetNotPreserved (Warning)

`datetimeoffset` stores each value's UTC offset as well as the instant. `timestamptz` stores
the instant only (and shows it in the session's time zone). Code that reads the original offset
(`DATEPART(tz, …)`, `SWITCHOFFSET`) needs another column to keep it.

### S2P1103 RowversionSemantics (Warning)

SQL Server sets a `rowversion` column on every insert and update. In PostgreSQL it is an
ordinary `bytea` column holding the copied values and nothing updates it. Optimistic
concurrency that compares it needs a trigger that sets a new value, or the system column `xmin`.

### S2P1104 SqlVariantAsText (Warning)

`sql_variant` holds values of different types in one column. It becomes `text` holding each
value's string form; the type of each value is lost.

### S2P1105 HierarchyIdAsText (Warning)

`hierarchyid` becomes `text` holding its string form (`/1/3/`). Ordering by the text is not
the same as ordering by the hierarchy, and its methods (`GetAncestor`, `IsDescendantOf`,
`GetLevel`, …) must be rewritten.

### S2P1106 SpatialAsWkb (Warning)

The `postgis` extension is disabled in the options, so a `geography`/`geometry` column becomes
`bytea` holding well-known binary. Enable `postgis` to keep it as a spatial type.

### S2P1107 VectorAsArray (Warning)

The `vector` (pgvector) extension is disabled, so a `vector(n)` column becomes `real[]`.
Enable `vector` to keep it as a vector type with distance operators.

### S2P1199 UnknownType (Error)

There is no mapping for the column's type. The column is written as `text`; add a
`typeOverrides` entry (`"schema.table.column": "<postgres type>"`) to choose a type.

### S2P1301 CaseInsensitiveCollation (Info)

The column's collation is case-insensitive (`…_CI_…`), so SQL Server compares `'a' = 'A'` as
equal. PostgreSQL's default collations are case-sensitive. With
`caseInsensitive: "icuCollation"` the column gets a nondeterministic ICU collation instead.

### S2P1302 LikeOnNondeterministic (Warning)

PostgreSQL before 18 does not allow `LIKE` on a column with a nondeterministic collation. Target
PostgreSQL 18, or keep the default collation for columns searched with `LIKE`.

### S2P1401 ExtensionRequired (Info)

The object uses one of the permitted extensions (named in the message). The generated script
creates it with `CREATE EXTENSION IF NOT EXISTS`; the target server must have it installed.

## S2P3xxx — expressions and built-in functions

### S2P3001 UnsupportedExpression (Error)

The expression uses T-SQL the translator does not handle yet (the message names the part). A
default, check constraint, computed column or index filter containing it is left out of the
PostgreSQL definition and kept beside it as a `/* sql2pg:untranslated … */` comment. Write the
PostgreSQL equivalent by hand.

### S2P3002 NotSequential (Info)

`NEWSEQUENTIALID()` produces increasing GUIDs. Before PostgreSQL 18 it becomes
`gen_random_uuid()`, whose values are random: inserts are no longer in key order. On PostgreSQL
18 it becomes `uuidv7()`, which is time-ordered.

### S2P3012 FloatRounding (Warning)

`ROUND` on a `float` or `real` rounds the binary value in SQL Server: `2.55` is stored as
`2.5499…`, so `ROUND(2.55E0, 1)` is `2.5`. PostgreSQL can round only `numeric`, and the value
cast to `numeric` is the decimal `2.55`, which rounds to `2.6`. Values that end in 5 at the
rounding digit can differ by one unit in the last place.

## S2P9xxx — internal errors

### S2P9001 InternalError (Error)

The converter threw an exception while converting the object. This is a bug in sql2pg, not in
the database. The object's original T-SQL is kept in the output as a comment. Please report it
with the message (no private schema needed: a minimal `CREATE` statement that reproduces it is
best).
