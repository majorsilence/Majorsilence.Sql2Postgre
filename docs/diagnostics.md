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

### S2P0201 NotYetSupported (Info)

Views are converted from Stage 2, and functions, procedures and triggers from Stage 3. Until
then they are listed in the output as skipped, so the report shows everything that remains.

### S2P0202 DependencyCycle (Warning)

Objects in one phase depend on each other in a cycle, so no order satisfies all of them. They
are emitted in name order; applying may need them run twice, or one rewritten to break the cycle.

### S2P0301 ExternalReference (Error)

The code names an object in another database (`otherdb.dbo.t`) or on a linked server
(`srv.db.dbo.t`). A PostgreSQL database cannot query another one directly; bring that data into
this database, or reach it with an extension the maintainer approves.

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

## S2P2xxx — schema objects

### S2P2001 GeneratedColumnStored (Info)

A computed column that is not `PERSISTED` is calculated on read in SQL Server. PostgreSQL 18
has the same (`VIRTUAL`); before 18 a generated column is always `STORED`, so it takes space and
is calculated on write. Values are the same.

### S2P2002 ClusteredIgnored (Info)

SQL Server keeps a table's rows in its clustered index order. PostgreSQL has no clustered
indexes: the index is an ordinary B-tree. `CLUSTER table USING index` reorders the rows once if
that matters for performance.

### S2P2003 IndexTypeUnsupported (Warning)

Columnstore, XML, full-text and memory-optimized hash indexes have no PostgreSQL equivalent, nor
do spatial indexes when `postgis` is disabled. The index is not created; queries still work but
may be slower. Full-text search is planned for Stage 4.

### S2P2004 ConstraintNotValid (Info)

SQL Server does not trust the constraint (it was added `WITH NOCHECK`), so existing rows may
break it. It is created `NOT VALID`: PostgreSQL checks new and changed rows only. Run
`ALTER TABLE … VALIDATE CONSTRAINT …` once the data is cleaned.

### S2P2005 TableTypeAsComposite (Info)

A table type (`CREATE TYPE … AS TABLE`) becomes a composite type. Procedures that take it as a
table-valued parameter need it passed as an array of that type; Stage 4 covers those.

### S2P2006 SequenceTypeChanged (Info)

PostgreSQL sequences are `smallint`, `integer` or `bigint`. A `decimal`/`numeric` sequence
becomes `bigint`, with its bounds limited to bigint's range.

### S2P2007 ComputedColumnTypeUnknown (Error)

A computed column read from a script has no declared type, and its expression does not make one
evident (for example it calls a function). The column is left out of the table and kept as a
comment. Add `"typeOverrides": { "schema.table.column": "<postgres type>" }`, or read from the
live database, whose catalog knows the type.

### S2P2008 IdentityTypeChanged (Info)

SQL Server allows `IDENTITY` on `decimal(p,0)`/`numeric(p,0)`; a PostgreSQL identity column must
be `smallint`, `integer` or `bigint`. The column becomes `integer` (p ≤ 9) or `bigint`.

### S2P2009 GeneratedNotImmutable (Error)

A non-persisted computed column may use values that change (`GETDATE()`, `NEWID()`); a
PostgreSQL generated column must be immutable. The column is left out and kept as a comment.
Compute the value in the query that reads it, or use a view.

### S2P2010 ComputedColumnTypeInferred (Info)

A computed column read from a script has no declared type; the type shown was worked out from
its expression. Exact decimals become an unconstrained `numeric`, since SQL Server's
result-precision rules do not carry over. Override it if a specific precision matters.

### S2P2011 RowGuidColIgnored (Info)

`ROWGUIDCOL` marks a column for SQL Server replication and `$ROWGUID`. PostgreSQL has neither;
the column stays an ordinary `uuid`.

### S2P2012 SparseIgnored (Info)

`SPARSE` saves space for mostly-NULL columns in SQL Server. PostgreSQL stores NULLs in a bitmap
already, so the column is created normally.

### S2P2013 DisabledConstraintEnforced (Warning)

The constraint is disabled in SQL Server (`NOCHECK CONSTRAINT`), so SQL Server does not check
it at all. It is created `NOT VALID`, which skips existing rows, but PostgreSQL still checks
every new and changed row. Drop the constraint if it should stay off.

### S2P2014 UsesUntranslatedColumn (Error)

A primary key, unique constraint, check, foreign key or index uses a column that was left out of
the table because its definition could not be translated (usually a computed column calling a
user-defined function or a `hierarchyid` method). It is kept as a comment. Once the column has
been added by hand, create this object too.

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

### S2P3003 ConvertStyleUnsupported (Error)

`CONVERT` with a style the translator does not know, or a style that is not a literal. The date
and time styles 1–5, 10–12, 20–25, 101–105, 108, 110–112, 114, 120, 121 and 126 are translated
(to `to_char` / `to_date` / `to_timestamp`), and money styles 1 and 2. Write the conversion by
hand with `to_char`.

### S2P3004 FormatUnsupported (Error)

`FORMAT` with a culture other than en-US, a standard format letter (`'d'`, `'C'`, …) or a custom
specifier the translator does not know. Custom date patterns (`yyyy-MM-dd HH:mm`) and the
`N`/`F` number formats are translated to `to_char`.

### S2P3005 DateFirstAssumed (Info)

`DATEPART(weekday, …)` and `DATEPART(week, …)` depend on `SET DATEFIRST`. The translation
assumes SQL Server's default, 7 (weeks start on Sunday, which is day 1). Code that sets
`DATEFIRST` differently needs the expression adjusted.

### S2P3006 ProceduralGlobal (Error)

`@@ROWCOUNT`, `@@ERROR`, `SCOPE_IDENTITY()`, the `ERROR_*()` functions and the like describe the
statement or error just before them, so they mean something only in procedural code, which
Stage 3 translates.

### S2P3007 JsonPathUnsupported (Error)

`JSON_VALUE`/`JSON_QUERY` with a path that is not a simple literal `$.a.b[2]` path (a `strict`
path, a wildcard, or a variable). Rewrite with PostgreSQL's `jsonb_path_query`.

### S2P3008 HashEncodingDiffers (Warning)

`HASHBYTES` on an `nvarchar` hashes its UTF-16 bytes in SQL Server; the translation hashes the
text's UTF-8 bytes, so the same text gives a different hash. Stored hashes computed by SQL Server
will not match; hash `varchar` (single-byte) text, or recompute the stored hashes.

### S2P3009 ExtensionDisabled (Error)

The construct needs one of the permitted extensions (`pgcrypto` for `HASHBYTES('SHA1')` and
`CRYPT_GEN_RANDOM`, `vector` for `VECTOR_DISTANCE`), and the options disable it.

### S2P3010 AmbiguousPlus (Warning)

The operands of `+` have no known type (a column the translator could not resolve, for
example), so it is kept as addition. If they are strings, it must be `||`.

### S2P3012 FloatRounding (Warning)

`ROUND` on a `float` or `real` rounds the binary value in SQL Server: `2.55` is stored as
`2.5499…`, so `ROUND(2.55E0, 1)` is `2.5`. PostgreSQL can round only `numeric`, and the value
cast to `numeric` is the decimal `2.55`, which rounds to `2.6`. Values that end in 5 at the
rounding digit can differ by one unit in the last place.

### S2P3013 UnresolvedColumn (Warning)

A column reference could not be resolved to a table, view or derived table in scope, so it is
written by its name without knowing its type (and conversions that depend on the type are not
made). Usually the column belongs to an object the reader did not see.

### S2P3014 ByteLengthDiffers (Warning)

`DATALENGTH` of an `nvarchar` counts UTF-16 bytes (2 per character) in SQL Server; `octet_length`
counts UTF-8 bytes, so the results differ for non-ASCII text and by a factor of two for ASCII.

### S2P3015 CollationIgnored (Info)

A `COLLATE` clause in an expression is dropped; the comparison uses the column's own collation.

### S2P3020 CompatDisabled (Error)

The translation needs an `mssql_compat` helper (for `DATEDIFF`, `CHARINDEX`, `ISNUMERIC`,
`TRY_CAST`, …) and `useCompatSchema` is false. Enable it, or rewrite the expression by hand.

### S2P3030 SpatialMethod (Error)

A spatial method (`STDistance`, `STIntersects`, `geography::Point`, …). Mapping these to PostGIS
`ST_*` functions is planned for Stage 4.

## S2P4xxx — queries and DML

### S2P4001 TopPercent (Info)

`TOP n PERCENT` becomes `LIMIT (SELECT ceil(count(*) * n / 100.0) FROM (…))`: the query is run
once to count its rows and again to return them.

### S2P4002 HintDropped (Info)

Table hints (`WITH (NOLOCK)`, `INDEX(…)`), join hints (`LOOP`, `HASH`, `MERGE`) and `OPTION (…)`
are dropped: PostgreSQL's planner takes no hints. `NOLOCK`'s dirty reads have no PostgreSQL
counterpart; readers never block writers there anyway.

### S2P4003 InsertExec (Error)

`INSERT … EXEC procedure` needs the procedure translated (Stage 3), as a function the INSERT can
select from.

### S2P4004 IdentityInsert (Info)

`SET IDENTITY_INSERT … ON` is not needed: identity columns are `GENERATED BY DEFAULT`, so they
accept explicit values. Afterwards the identity's sequence must be moved past those values.

### S2P4005 QuirkyUpdate (Error)

`UPDATE … SET @v = column = expression` assigns a variable row by row in an order SQL Server does
not guarantee. Rewrite it with a window function.

### S2P4008 UpdateFromOuterJoin (Error)

The table an `UPDATE` or `DELETE` changes sits inside an outer join of its `FROM` clause.
PostgreSQL names the target outside `FROM`, so only inner and cross joins can be rearranged.
Rewrite the outer join as a subquery in `WHERE`.

### S2P4009 DmlTop (Error)

`UPDATE`/`DELETE`/`INSERT`/`MERGE` with `TOP (n)` change an arbitrary n rows. Rewrite with a key
subquery (`WHERE id IN (SELECT id … LIMIT n)`).

### S2P4010 ForXmlUnsupported (Error)

`FOR XML` that builds XML (`RAW`, `AUTO`, `EXPLICIT`, or `PATH` with named elements). Only the
`FOR XML PATH('')` string-concatenation idiom is translated (to `string_agg`).

### S2P4011 ForJsonUnsupported (Error)

`FOR JSON AUTO` over joined tables (it nests by table), over `SELECT *`, or with an unnamed
column. `FOR JSON PATH` (dotted names, `ROOT`, `INCLUDE_NULL_VALUES`, `WITHOUT_ARRAY_WRAPPER`) and
`AUTO` over one table are translated; the JSON text is spaced differently (`{"a" : 1}`), the
same JSON.

### S2P4012 ViewOrderIgnored (Info)

A view's `TOP 100 PERCENT … ORDER BY` is dropped; SQL Server does not honour that order either.

### S2P4013 OutputInto (Error)

`OUTPUT … INTO` a table or table variable writes the changed rows somewhere: procedural code,
translated in Stage 3.

## S2P7xxx — data copy and verification

### S2P7001 NulCharacterRemoved (Warning)

SQL Server strings may contain the NUL character (`CHAR(0)`); PostgreSQL text cannot. Each NUL
is removed during the copy, and the message says how many in which column. Values that differed
only by NULs are now equal, which can break a unique index.

### S2P7002 SpatialZmDropped (Warning)

Some spatial values had Z (elevation) or M (measure) coordinates. The copy reads them as
well-known binary, which carries X and Y only, so Z and M are lost.

### S2P7003 TableCopyFailed (Error)

Copying a table, or setting a sequence's position, failed; the message has the error. Other
tables are still copied. Fix the cause and run the copy again: tables already finished (with
`--state`) are skipped, and this one is cleared and copied afresh.

### S2P7101 RowCountMismatch (Error)

The table has a different number of rows in PostgreSQL than in SQL Server. Usually the copy
failed part-way (see `S2P7003`) or rows changed in SQL Server after the copy; copy the table
again.

### S2P7102 ChecksumMismatch (Error)

The table has the same number of rows on both sides, but some values differ after allowing for
the documented transforms (rounding, UTC, NUL removal…). Compare the table's rows to find which;
rows changed in SQL Server after the copy are the usual cause.

## S2P9xxx — internal errors

### S2P9001 InternalError (Error)

The converter threw an exception while converting the object. This is a bug in sql2pg, not in
the database. The object's original T-SQL is kept in the output as a comment. Please report it
with the message (no private schema needed: a minimal `CREATE` statement that reproduces it is
best).
