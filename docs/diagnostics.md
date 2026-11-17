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

### S2P0203 DependsOnUnconverted (Error)

The view uses something that is not converted: a function or procedure (routines are converted
in Stage 3), or another view that needs a person. It cannot be created until that exists, so it
is held back, and the views built on it with it.

### S2P0204 SessionStatementDropped (Info)

A script's `USE` or session `SET` option (`NOCOUNT`, `ANSI_NULLS`, `DATEFORMAT`, `ROWCOUNT`,
`TRANSACTION ISOLATION LEVEL`, …) is dropped. A PostgreSQL connection stays in one database; set
an isolation level with `BEGIN ISOLATION LEVEL …` where it matters.

### S2P0205 DdlInScript (Error)

`sql2pg translate` met DDL (`CREATE TABLE`, `ALTER TABLE`, `CREATE INDEX`, …). Schema objects are
converted by `sql2pg schema`, which orders them and creates their keys and indexes after the data;
the statement is kept as a comment.

### S2P0206 ProceduralStatement (Error)

A procedural statement that is not translated yet (DDL has its own code, S2P5070). Variables, `SET`, `IF`, `WHILE`, `PRINT`, `RAISERROR`,
`THROW`, `EXEC` of a procedure, result sets, transaction control, `TRY … CATCH`, cursors,
dynamic SQL (S2P5060 when it cannot be), temp tables and table variables are translated. `FETCH` without `INTO` (which returns the row as a result set), a cursor used
without being declared in the same code, and `WHERE CURRENT OF` with joined tables also get
this code.
`ALTER TABLE` on a temp table and computed columns in one also get this code. In a procedure, a result set inside a loop,
rows returned by DML with `OUTPUT`, and any statement other than a result set under
`SET ROWCOUNT n` also get this code. The statement is kept as a comment.

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

`hierarchyid` becomes `text` holding its string form (`/1/3/`). Its methods become `mssql_compat`
helpers over that text: `GetLevel`, `GetAncestor`, `IsDescendantOf`, `GetDescendant` and
`GetReparentedValue`. `ToString`, `Parse` and a cast are the text itself, and `GetRoot` is `/`.
A computed column that calls one is `STORED`, since PostgreSQL 18 allows no user-defined function
in a `VIRTUAL` one.

`ORDER BY` a hierarchyid and `<`/`>` between two compare `mssql_compat.hierarchyid_key(x)`,
which orders depth-first as SQL Server does (a parent before its children, `/2/` before `/10/`,
dotted and negative labels in their places). Some things still order the text: an index on the
column, `MIN`/`MAX`, and `ORDER BY` on an output alias of one. Use the key there if the order
matters.

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

A table type (`CREATE TYPE … AS TABLE`) becomes a composite type. Its keys, indexes, defaults
and identity are not kept; a caller supplies every value.

- **A table-valued parameter** (`@t dbo.T READONLY`) becomes `t dbo.t[]`. The body reads `@t` as
  `unnest(routine.t)`, in procedures and inline functions alike.
- **A variable of the type** (`DECLARE @t dbo.T`) becomes a table variable with its columns. Passed
  to a routine, it becomes `ARRAY(SELECT ROW(…)::type FROM it)`, through a helper variable for a
  CALL, which allows no subquery.
- **From other SQL**, pass `ARRAY[ROW(…)::type, …]`.
- **From application code**, pass an array of a mapped composite (S2P8003).

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

### S2P2015 MaterializedViewRefresh (Warning)

An indexed view becomes a materialized view. SQL Server keeps an indexed view's rows current as
the tables change; PostgreSQL stores a materialized view's rows when it is created and on
`REFRESH MATERIALIZED VIEW`, so something must refresh it (after loads, or on a schedule).

### S2P2016 ViewOptionDropped (Info)

`WITH SCHEMABINDING` and `VIEW_METADATA` are dropped. PostgreSQL views always depend on what
they read (a table a view uses cannot be dropped without `CASCADE`), which is what
SCHEMABINDING asks for.

### S2P2020 SystemVersioning (Info)

The table has a `PERIOD FOR SYSTEM_TIME`. PostgreSQL has no temporal tables, so two row
triggers do SQL Server's work:

- `<table>_period` (`BEFORE INSERT OR UPDATE`) sets the period start to the transaction's start
  time in UTC (`now() AT TIME ZONE 'UTC'`, as SQL Server stamps it). It sets the period end to
  `9999-12-31 23:59:59.999999`, the end of time at the column's precision (PostgreSQL keeps
  microseconds).
- `<table>_history` (`AFTER UPDATE OR DELETE`, only with `SYSTEM_VERSIONING = ON`) copies the
  old row into the history table, its period ended at that time.

The history table is an ordinary table, and the data copy keeps both tables' period values.
Unlike SQL Server, PostgreSQL lets code write the period columns and the history table; the
period trigger overwrites what is written to the current table. Without `SYSTEM_VERSIONING`,
or when the history table is not in the source, only the period trigger is created. A script
that versions a table without naming `HISTORY_TABLE` gets this code as a Warning: the table
SQL Server creates is not in the script.

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

From text, SQL Server also reads some year-first strings under a style that is not year-first,
and the translation does the same (established on SQL Server 2022):

- `yyyymmdd`, always;
- for datetime and smalldatetime, `yyyy-a-b` (with a time or not) as year, then month and day in
  the style's order, so `2000-06-01` is 6 January under 103;
- for date and datetime2, ISO 8601 with a `T`.

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

### S2P3040 XmlModify (Error)

The xml `modify()` method (`insert`, `replace value of`, `delete`), in `UPDATE … SET
x.modify(…)` or `SET @x.modify(…)`. PostgreSQL's xml type has no update functions. Build the new
document instead, in the application or with `xmlelement`/`xmlconcat`, and assign it. For a
value inside it, store that value in a column of its own.

### S2P3041 XQueryUnsupported (Error)

An xml method's path uses XQuery that XPath 1.0 lacks, which is all PostgreSQL evaluates. That
covers FLWOR (`for`, `let`, `return`), variables, `sql:column`/`sql:variable`, `data()`,
`xs:` casts, constructors, conditionals, and a path that is not a literal.

The rest is translated, over `XMLTABLE`:

- `value(path, type)` is a column of that type;
- `exist(path)` is `boolean(path)`, as a bit;
- `query(path)` is an xml column, empty when nothing matches;
- `nodes(path)` in `CROSS`/`OUTER APPLY` is an XMLTABLE in FROM, one xml row per node.

A `nodes()` row is an element, so paths under it start there, as in SQL Server. An absolute path
under a `nodes()` row reads from that element as the root: it works for nodes taken from the
document's root element.

The prolog's `declare namespace` become `XMLNAMESPACES`. PostgreSQL has no default namespace, so
`declare default element namespace` gives every unprefixed element name a prefix of its own.
`string(.)[1]`, XQuery's singleton marker after a function, loses its `[1]`.

## S2P4xxx — queries and DML

### S2P4001 TopPercent (Info)

`TOP n PERCENT` becomes `LIMIT (SELECT ceil(count(*) * n / 100.0) FROM (…))`: the query is run
once to count its rows and again to return them.

### S2P4002 HintDropped (Info)

Table hints (`WITH (NOLOCK)`, `INDEX(…)`), join hints (`LOOP`, `HASH`, `MERGE`) and `OPTION (…)`
are dropped: PostgreSQL's planner takes no hints. `NOLOCK`'s dirty reads have no PostgreSQL
counterpart; readers never block writers there anyway.

### S2P4003 InsertExec (Error)

`INSERT … EXEC procedure` works when the procedure is converted to a set-returning function
(one result set, no OUTPUT parameters, status code or transaction control): it becomes
`INSERT … SELECT * FROM f(…)`. When the procedure returns its rows through refcursors, the
statement is kept as a comment.

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

A statement has both `OUTPUT … INTO` and a plain `OUTPUT`. `OUTPUT … INTO` alone is translated:
the statement becomes a data-modifying CTE with `RETURNING`, and an `INSERT` writes its rows to
the table, temp table or table variable.

### S2P4014 SystemTimeQuery (Info)

`FROM t FOR SYSTEM_TIME …` becomes a subquery under the table's name or alias. It reads the
table and its history table (`UNION ALL`), each filtered as SQL Server defines the form, with
start and end the period columns:

| Form | Rows whose period |
|---|---|
| `AS OF t` | `start <= t AND end > t` |
| `FROM a TO b` | `start < b AND end > a` |
| `BETWEEN a AND b` | `start <= b AND end > a` |
| `CONTAINED IN (a, b)` | `start >= a AND end <= b` |
| `ALL` | every row |

It is a Warning when the table has no history table in the source: only the current rows are
read. Times are UTC, as in SQL Server.

## S2P5xxx — procedural code

Parameters keep their names and are written `<routine>.<name>` in the body; local variables
become `v_<name>`, all declared at the top of the body, and `#variable_conflict use_column`
makes a bare name a column, as it is in T-SQL. A script batch that uses variables or control
flow becomes a `DO` block.

### S2P5001 VariableRedeclared (Error)

A variable is declared twice with different types. A PL/pgSQL body declares each variable once;
rename one of them.

### S2P5002 SelectAssignmentOrder (Warning)

`SELECT @v = col FROM …` over a query that may return several rows and has no ORDER BY. Like
SQL Server, the translation assigns from every row in turn (a `FOR` loop), so the last row's
values remain, but which row comes last is arbitrary on both engines. Add an ORDER BY, or a
condition that selects one row.

### S2P5003 ErrorStopsExecution (Info)

The routine runs statements after a DML statement without `SET XACT_ABORT ON` or `TRY`. SQL
Server continues after most statement-level errors; PostgreSQL stops the routine at the first
error and rolls back to the caller. Code that checks `@@ERROR` after each statement relied on the
first behaviour.

### S2P5004 ErrorGlobal (Warning)

`@@ERROR` outside a `CATCH` block is translated as `0`: if the previous statement had failed,
PostgreSQL would already have left the routine. Error handling belongs in `TRY … CATCH`.

### S2P5005 GotoLabel (Error)

`GOTO` and statement labels have no PL/pgSQL equivalent. Rewrite the jump as a loop with `EXIT`
or `CONTINUE`, or as `IF` blocks.

### S2P5006 SelectAssignmentAccumulates (Error)

`SELECT @v = @v + … FROM …` builds a value row by row. The translation handles this with a loop
over the rows when the expression uses only the rows' columns; with aggregates, grouping,
DISTINCT or subqueries it cannot. Rewrite with an aggregate (`STRING_AGG`, `SUM`).

### S2P5007 RaiserrorStops (Info)

`RAISERROR` with severity 11 or more, outside `TRY`, and not followed by `RETURN`: SQL Server
reports the error and continues with the next statement; PostgreSQL's `RAISE EXCEPTION` ends the
routine.

### S2P5008 RaiserrorUntranslatable (Error)

`RAISERROR` with a message number (its text is in `sys.messages`, which PostgreSQL does not
have), or with a message held in a variable and formatted with arguments. Write the message as a
literal, or use `THROW`.

### S2P5009 ResultSetInBlock (Error)

A script batch that uses variables or control flow is translated as a `DO` block, and a `DO`
block cannot return rows. The statement that would return rows (a `SELECT`, or `OUTPUT` without
`INTO`) is kept as a comment. Turn the batch into a function, or select into a temporary table and
query it after the block.

### S2P5010 FunctionColumnTypeUnknown (Error)

An inline table-valued function's result column has no type the converter can work out (an
expression over something it cannot type), so `RETURNS TABLE (…)` cannot be written. Cast the
column to its type in the function's query.

### S2P5011 RaiserrorFormat (Warning)

The `RAISERROR` message uses flags, a width, a precision, or `%x`/`%o`. PostgreSQL's `RAISE`
substitutes each argument in its plain text form at `%`, so the padding or base is lost.

### S2P5012 ParameterRenamed (Info)

A table-valued function has a parameter named like one of its result columns
(`@PersonID` and a `PersonID` column). PostgreSQL does not allow that, so the parameter becomes
`p_<name>`. Callers that pass arguments by position are unaffected; named arguments must use the
new name.

### S2P5020 ParameterDefaultAdded (Info)

In PostgreSQL every parameter after one with a default must have a default too; SQL Server has
no such rule. The later parameters get `DEFAULT NULL`, which changes nothing for callers that
pass every argument.

### S2P5021 ResultSetCursor (Warning)

The procedure returns rows and could not become a set-returning function. It has OUTPUT
parameters, a status code, `COMMIT`/`ROLLBACK`, or more than one result set; or the options ask
for `Refcursor`. Each result set comes back through an `INOUT … refcursor` parameter
(`result_set`, or `result_set_1`, `result_set_2`, … when there are several), opened with
`OPEN … FOR <select>`. Callers change: call the procedure and `FETCH ALL FROM` each cursor in the
same transaction. Passing your own names makes that easier:
`CALL p(…, result_set_1 => 'c1'); FETCH ALL FROM c1;`. A call from another converted procedure
passes the caller's own cursors on.

### S2P5022 ReturnCodeParameter (Info)

The procedure ends with `RETURN <value>`. PostgreSQL procedures return no value, so the status
code comes back in an extra `INOUT return_code integer DEFAULT NULL` parameter. It is set to 0
at the start, as SQL Server returns 0 when `RETURN` gives none. `EXEC @rc = p` in converted code
reads it.

### S2P5023 SystemProcedure (Error)

`EXEC sp_…` or `EXEC xp_…` calls a SQL Server system procedure, which PostgreSQL does not have.
The call is kept as a comment: rewrite it with the PostgreSQL feature that does the same job
(`ALTER … RENAME` for `sp_rename`, the catalogs for `sp_help`, and so on). `sp_executesql` is
dynamic SQL (S2P0206 until it is translated).

### S2P5024 Savepoint (Error)

`SAVE TRANSACTION`, or `ROLLBACK TRANSACTION` to a savepoint name, inside a routine. PL/pgSQL has
no savepoint statements. The nearest equivalent is a `BEGIN … EXCEPTION … END` block, which rolls
back what its body did when an error is caught. In an ad-hoc script outside a `DO` block, the
statements become `SAVEPOINT` and `ROLLBACK TO SAVEPOINT`.

### S2P5025 TranCount (Error)

`@@TRANCOUNT` outside a `CATCH` block. It counts nested `BEGIN TRANSACTION`s, and PostgreSQL
has one transaction and no count. A routine always runs inside a transaction, so the test has no
translation. The statement using it is kept as a comment. Inside `CATCH` it is translated
(S2P5041), and `XACT_STATE()` is translated everywhere (S2P5041).

### S2P5026 BeginTransactionDropped (Info)

`BEGIN TRANSACTION` inside a procedure or a `DO` block becomes `COMMIT`. When an application
calls the procedure outside a transaction, SQL Server has already committed each statement
before `BEGIN TRANSACTION` on its own, and `COMMIT` keeps that work as well.
A new transaction then starts. Inside `TRY` the `COMMIT` goes just before the `TRY` block
(PL/pgSQL cannot end a transaction inside a block with an exception handler).

`COMMIT` and `ROLLBACK` become PL/pgSQL's `COMMIT` and `ROLLBACK`. They work when the procedure
is called with `CALL` outside an explicit transaction block, and fail ("invalid transaction
termination") inside one. A procedure that ends a transaction, or calls one that does, is never
converted to a function. A second `BEGIN TRANSACTION` nested inside the first commits the outer
transaction's work so far, which SQL Server would not.

### S2P5027 UnknownProcedure (Error)

`EXEC` names a procedure that is not in the source database (it lives in another database, was
excluded, or does not exist). The call is kept as a comment.

### Temp tables and table variables

Both become PostgreSQL temporary tables, which belong to the session:

- `#t` is the temporary table `t`. A routine runs `DROP TABLE IF EXISTS t` before it creates the
  table, and drops it again when it ends or returns, as SQL Server does. The exception is a
  procedure that returns refcursors: they read the table after the procedure has ended, so it
  stays until the next call or the end of the session. A script's temp tables last as long as
  the session, as in SQL Server.
- `@t` is the temporary table `tv_<routine>_<name>`, or `tv_<name>` in a script. It is created
  at the start of the body, since T-SQL declares a table variable once however often its
  `DECLARE` runs. A procedure or script batch drops it when it ends; a function's is
  `ON COMMIT DROP`.
- Columns, keys, `CHECK` constraints and indexes are kept. Index names are left to PostgreSQL,
  since they share the temporary schema with the tables.
- `SCOPE_IDENTITY()` follows inserts into temp tables and table variables with an identity
  column. As in SQL Server, inserting rows into a table without one sets it to NULL.
- `OBJECT_ID('tempdb..#t')` is `to_regclass('pg_temp.t')`, as an integer.
- `TRUNCATE TABLE` restarts the table's identity (`RESTART IDENTITY`), as SQL Server's does.
- Creating a temporary table makes a function `VOLATILE`. It cannot run in a read-only
  transaction or on a hot standby.

### TRY … CATCH

`BEGIN TRY … END TRY BEGIN CATCH … END CATCH` becomes a block with an exception handler:
`BEGIN … EXCEPTION WHEN OTHERS THEN … END;`.

- When a statement in the TRY fails, PostgreSQL undoes everything the TRY did and runs the
  handler. The SQL Server code usually does the same with `ROLLBACK` in `CATCH`. See S2P5044 for
  where the two differ.
- The handler starts with `GET STACKED DIAGNOSTICS` for the `ERROR_*()` functions the CATCH block
  uses. `ERROR_MESSAGE()` is the message, which for errors PostgreSQL raises is PostgreSQL's
  wording. A nested CATCH block has its own variables, so `ERROR_*()` after it reads the outer
  error again, as in SQL Server.
- `ERROR_NUMBER()` maps the SQLSTATE to SQL Server's number:

  | Error | SQLSTATE | Number |
  |---|---|---|
  | Unique or primary key violation | 23505 | 2627 |
  | Foreign key violation | 23503 | 547 |
  | Check violation | 23514 | 547 |
  | NULL into a NOT NULL column | 23502 | 515 |
  | Division by zero | 22012 | 8134 |
  | Arithmetic overflow | 22003 | 8115 |
  | String data would be truncated | 22001 | 8152 |
  | Conversion failed | 22P02 | 245 |
  | Date/time conversion failed | 22007 | 241 |
  | Date/time out of range | 22008 | 242 |
  | Deadlock or serialization failure | 40P01, 40001 | 1205 |

  `THROW n, …` keeps its number. `RAISERROR` with a message, and anything else, is 50000.
- `ERROR_SEVERITY()` is 16. `ERROR_PROCEDURE()` is the procedure's schema-qualified name, as
  SQL Server 2017 and later return it.
- `THROW;` re-raises the error as it was (`RAISE;`).
- In a TRY, `BEGIN TRAN` commits the work before the TRY block, and a `COMMIT` ending the TRY
  runs right after the block. `COMMIT` and `ROLLBACK` in the handler stay as they are. With these,
  the usual patterns end their transactions as SQL Server does:
  - `BEGIN TRY BEGIN TRAN … COMMIT END TRY BEGIN CATCH IF XACT_STATE() <> 0 ROLLBACK; THROW; END CATCH`
  - `BEGIN TRAN; BEGIN TRY … COMMIT END TRY BEGIN CATCH ROLLBACK … END CATCH`

### Cursors

- **The usual loop.** `DECLARE c CURSOR FOR …; OPEN c; FETCH NEXT FROM c INTO @a, @b;
  WHILE @@FETCH_STATUS = 0 BEGIN … FETCH NEXT FROM c INTO @a, @b; END; CLOSE c; DEALLOCATE c;`
  becomes `FOR … IN <query> LOOP … END LOOP;`. The cursor must be used nowhere else (no other
  `FETCH`, no `WHERE CURRENT OF`), and the loop must have no `CONTINUE`. In T-SQL a `CONTINUE`
  would skip the `FETCH`.
- **Any other use** of a cursor is a `refcursor` variable named `cur_<name>`:
  - `OPEN` opens it with the cursor's query. Variables in the query are read at that point, as
    in T-SQL.
  - It is `SCROLL` when the cursor is declared `SCROLL`, `STATIC`, `KEYSET` or `DYNAMIC`, or
    when a `FETCH` moves other than forward. `FETCH NEXT`, `PRIOR`, `FIRST`, `LAST`, `ABSOLUTE`
    and `RELATIVE` are kept.
  - `CLOSE` is kept, and `DEALLOCATE` produces nothing.
  - Cursor variables (`DECLARE @c CURSOR; SET @c = CURSOR FOR …`) work the same way.
- **FETCH and FOR loops read into helper variables.** The T-SQL variables take the row only when
  there is one. PL/pgSQL would set them to NULL past the end of the rows; T-SQL leaves them as
  they were.
- **@@FETCH_STATUS** is 0 or -1, set by each `FETCH`.
- **WHERE CURRENT OF c** is kept, and the cursor's query gets `FOR UPDATE`, which PostgreSQL
  needs to locate the row.
- **Changes made while a cursor is open.** A PostgreSQL cursor reads the rows as they were when
  it was opened. A T-SQL `DYNAMIC` cursor, which is the default, also sees rows that change or
  appear while it is open.

### S2P5030 GlobalTempTable (Error)

A global temporary table (`##t`) is shared by every session. PostgreSQL's temporary tables
belong to one session, so there is no equivalent: use a real table, and delete its rows when
they are no longer needed.

### S2P5031 TableVariableRollback (Warning)

A routine with table variables runs `ROLLBACK`. SQL Server keeps a table variable's rows when a
transaction rolls back. In PostgreSQL the table variable is a temporary table, so the rollback
undoes its rows, and its creation too when that was in the same transaction. The table is
created again, empty, after the `ROLLBACK`. Code that logs into a table variable and writes
the log after rolling back loses those rows.

### S2P5032 TempTableReplaced (Warning)

A procedure creates a temp table and calls a procedure that creates one with the same name. In
SQL Server each procedure sees its own. In PostgreSQL there is one table of that name per
session: the callee's `DROP TABLE IF EXISTS` removes the caller's, and the caller's later
statements fail or read the wrong rows. Rename one of the two tables.

### S2P5033 TempTableForeignKey (Info)

A `FOREIGN KEY` on a temp table is dropped. SQL Server does not enforce foreign keys on
temporary tables either (it skips them with a warning).

### S2P5040 ErrorLineState (Info)

`ERROR_LINE()` is NULL, because PostgreSQL reports the line in the error's context text, not as
a number. `ERROR_STATE()` is 1, because PostgreSQL errors have no state number.

### S2P5041 TransactionState (Info)

`XACT_STATE()` outside a `CATCH` block is 1, because PL/pgSQL code always runs in an active,
committable transaction. SQL Server returns 0 outside a transaction, and -1 in code called from
a CATCH block whose transaction is doomed: AdventureWorks' `uspLogError` tests for -1.

`XACT_STATE()` or `@@TRANCOUNT` in a `CATCH` block is a constant:

- -1 (`XACT_STATE()`) or 1 (`@@TRANCOUNT`) when a `BEGIN TRAN` may have run, in the TRY or
  before it. A `ROLLBACK` under the test then undoes the work since that `BEGIN TRAN`. The TRY's
  own work is already undone.
- 0 when no `BEGIN TRAN` comes before. The test is then false, as in SQL Server, and earlier
  work is not rolled back.

### S2P5042 TransactionInTry (Error)

`COMMIT` or `ROLLBACK` inside `TRY`, other than a `COMMIT` that ends it, or `EXEC` of a procedure
that ends a transaction. PL/pgSQL cannot end a transaction inside a block with an exception
handler ("cannot commit while a subtransaction is active", "invalid transaction termination").
Move the transaction control out of the TRY, for example ending the TRY with the `COMMIT`.

### S2P5043 ErrorFunctionOutsideCatch (Warning)

An `ERROR_*()` function outside a `CATCH` block is NULL. That is what SQL Server returns when no
error is being handled. But an error-logging procedure called from a `CATCH` block (AdventureWorks'
`uspLogError`) reads the caller's error this way, and in PostgreSQL it sees NULL. Pass the
error number and message to such a procedure as parameters.

### S2P5050 GlobalCursor (Error)

A cursor declared `GLOBAL`, or used as `OPEN GLOBAL c`. A global cursor outlives the code that
declares it: another batch or procedure on the connection can fetch from it. A PL/pgSQL cursor
belongs to the routine that opens it. Declare it `LOCAL`, or return the rows another way.

### Dynamic SQL

`EXEC (string)`, `EXECUTE (string)` and `EXEC sp_executesql` become PL/pgSQL `EXECUTE`.

- **Where the text comes from.** It is the literal, or the string built from literal pieces. A
  variable (`EXEC (@sql)`) is traced back to the `SET @sql = …` or `DECLARE @sql … = …` that last
  gave it its value. Nothing else may assign the variable in between.
- **How it is translated.** The text is parsed and translated as T-SQL. It may hold SELECT,
  INSERT, UPDATE, DELETE, MERGE and TRUNCATE statements. The dynamic batch sees only its own
  parameters, as in SQL Server.
- **sp_executesql parameters.** `@p` becomes `$1`, `$2`, …, in the order of the parameter list.
  The caller's values go in `USING`, converted to the declared types.
  `SELECT @out = … ` into an OUTPUT parameter becomes `EXECUTE … INTO` the caller's variable.
- **Pieces that are not literals.** They are filled in with `format()`:
  - `QUOTENAME(x)` is a name (`%I`). It is mapped at run time as the converter maps names: lower
    case, and the schema map for a schema (S2P5061).
  - A piece between quotes (`'… = ''' + @v + ''''`) is a value (`%L`), which `format()` quotes. A
    value containing a quote therefore works, where in SQL Server it broke the statement.
  - Any other piece is spliced in as it is (`%s`). A number cast to text is fine. Other text is
    used as it stands (S2P5062).
- **Rows.** Rows from a dynamic SELECT are one of the procedure's result sets, returned through
  its refcursor (`OPEN … FOR EXECUTE`).

### S2P5060 DynamicSqlUntranslatable (Error)

The dynamic SQL is kept as a comment, for one of these reasons:

- Its text cannot be followed back to a literal. For example, it is a parameter, it is built
  with `+=`, or it is assigned in a loop.
- It does not parse.
- It contains statements other than queries and DML: DDL, `DECLARE`, `IF`, `EXEC`.
- It reads a variable it does not declare.
- It assigns something other than an OUTPUT parameter.
- A value is spliced into the middle of a string literal (`'%' + @v + '%'`).
- It names objects at run time under `NameStyle = SnakeCase`.

### S2P5061 DynamicIdentifier (Info)

A name in dynamic SQL comes from a variable through `QUOTENAME`. It is mapped at run time with
`lower()`, and, in schema position, the schema map (`dbo` → `public`). It must therefore name an
object as the converter named it.

### S2P5062 DynamicSqlSpliced (Warning)

Text from a variable is spliced into dynamic SQL as it stands (`%s`). SQL Server would have run
it as T-SQL. If it holds T-SQL syntax (a column list with `[brackets]`, `TOP`, `+` string
concatenation), PostgreSQL gets it unchanged, so check what the variable can hold.

### S2P5044 TryUndoesWrites (Warning)

A TRY changes data, outside a transaction, and has more statements after that. If a later
statement fails, SQL Server keeps the changes already made, because each was committed on its
own, and only the failing statement is undone. PostgreSQL's exception block undoes everything
the TRY did. Wrap the TRY's work in `BEGIN TRAN … COMMIT` if all-or-nothing is what the code
means. If it is not, split the TRY.

### S2P5070 DdlInRoutine (Error)

A procedure, function or trigger runs DDL that is not translated. That covers:

- `ALTER TABLE … SET (…)` other than `SYSTEM_VERSIONING`, and `SWITCH`;
- `CREATE`/`DROP` of a table, view or trigger;
- `ALTER INDEX … DISABLE` of an index the source model does not have;
- `NOCHECK`/`CHECK CONSTRAINT` of a constraint the routine neither added nor the model has;
- an unnamed constraint, and a column with a computed value or inline constraints;
- any other `CREATE`, `ALTER` or `DROP`.

Rewrite the statement for PostgreSQL, or move it out of the routine; it stays as a comment.

Index, constraint and column changes on real tables, and dropping procedures and functions, are
translated (S2P5071 to S2P5074). Temp tables are not affected: their DDL is translated. In an
ad-hoc script, DDL is S2P0205.

### S2P5071 IndexDdlInRoutine (Info)

Index DDL in a routine was translated:

- `CREATE INDEX` names the index, table and columns as the schema converter does. `CLUSTERED`,
  the filegroup or partition scheme, and `WITH` options are dropped. `DROP_EXISTING = ON`
  becomes a `DROP INDEX IF EXISTS` first.
- `DROP INDEX ix ON t` and `DROP INDEX t.ix` become `DROP INDEX schema.ix`, since PostgreSQL index
  names are per schema.
- `ALTER INDEX … REBUILD` creates the index from its definition in the source model when the index
  is missing (`DISABLE` dropped it), and is `REINDEX INDEX` otherwise.
- `ALTER INDEX … REORGANIZE` is `REINDEX INDEX`, which rebuilds the whole index (PostgreSQL has no
  lighter form). `ALTER INDEX ALL … REBUILD|REORGANIZE` is `REINDEX TABLE`.
- DDL on an index PostgreSQL does not have (a columnstore, xml or full-text index, S2P2003) is
  dropped.

### S2P5072 IndexDisabled (Warning)

`ALTER INDEX … DISABLE` drops the index: PostgreSQL cannot disable one. A later
`ALTER INDEX … REBUILD`, in the same routine or another, creates it again from its definition. If
the code disables an index to stop it being used rather than to speed up a load, the index is
gone until then.

### S2P5073 ConstraintDisabled (Warning)

PostgreSQL cannot switch a constraint off, so:

- `ALTER TABLE t NOCHECK CONSTRAINT c` (or `ALL`: the table's foreign keys and checks) becomes
  `ALTER TABLE t DROP CONSTRAINT IF EXISTS c`.
- `CHECK CONSTRAINT c` adds the constraint back `NOT VALID` when it is missing. Like SQL Server,
  this leaves the rows loaded in between unchecked (`is_not_trusted`). Under `WITH CHECK CHECK
  CONSTRAINT`, `VALIDATE CONSTRAINT` then checks them.

The definition comes from the source model, or from an `ADD CONSTRAINT` earlier in the routine.
Both statements work whichever routine runs the other. While the constraint is off, PostgreSQL
has no constraint at all; SQL Server keeps a disabled one.

### S2P5074 SchemaChangeInRoutine (Warning)

The routine changes the schema at run time:

- `ALTER TABLE … ADD` of columns (type, NOT NULL, default, identity) and of named primary key,
  unique, foreign key, check and default constraints (`WITH NOCHECK` → `NOT VALID`);
- `DROP COLUMN` and `DROP CONSTRAINT` (a default constraint becomes `ALTER COLUMN … DROP
  DEFAULT`);
- `ALTER COLUMN` (`TYPE … USING`, then `SET`/`DROP NOT NULL`: SQL Server makes the column nullable
  unless NOT NULL is given);
- `DROP PROCEDURE` and `DROP FUNCTION`, which become `DROP ROUTINE`, since a procedure may have
  become a function.

The converted schema, and the code converted against it, do not see these changes until the
routine runs. Statements in other routines that use an added column may need a second look.

### S2P5075 SystemVersioningInRoutine (Info)

A routine switches a temporal table's parts off or on, as load procedures do around a load.
Each becomes `ALTER TABLE … DISABLE`/`ENABLE TRIGGER` on the triggers of S2P2020:

- `ALTER TABLE t SET (SYSTEM_VERSIONING = OFF | ON (HISTORY_TABLE = h))` switches the history
  trigger. The `DATA_CONSISTENCY_CHECK` is not run.
- `ALTER TABLE t DROP | ADD PERIOD FOR SYSTEM_TIME` switches the period trigger. With it off,
  code may write the period columns, as SQL Server allows once the period is dropped.

Switching on versioning into a history table other than the source's, or on a table without a
period in the source, stays S2P5070.

## S2P6xxx — triggers

DML triggers are converted; each event (INSERT, UPDATE, DELETE) becomes a trigger function and
a `CREATE TRIGGER` of its own. A trigger with one event keeps its name; one with several gets
`_insert`, `_update` and `_delete`. This is because PostgreSQL allows transition tables only on
single-event triggers.

- **AFTER (and FOR) triggers** are `AFTER … FOR EACH STATEMENT` with
  `REFERENCING NEW TABLE AS inserted OLD TABLE AS deleted`.
  - Set-based T-SQL over `inserted` and `deleted` runs as it is, once per statement, as in SQL
    Server.
  - The pseudo-table an event does not have (`deleted` on INSERT, `inserted` on DELETE) is an
    empty query of the table (`SELECT * FROM t WHERE false`).
  - `@@ROWCOUNT` at the start is the number of rows the statement affected. As in SQL Server,
    `SET NOCOUNT ON` resets it, so test it first.
- **INSTEAD OF triggers on views** are `INSTEAD OF … FOR EACH ROW`. `inserted` is the one row
  (`SELECT NEW.*`), and so is `deleted` (`SELECT OLD.*`).
- **The function returns** NULL, or the row for an INSTEAD OF trigger, so that PostgreSQL counts
  it as done.
- **ROLLBACK** in a trigger raises an error ("transaction rolled back by trigger …"). That ends
  the statement and its transaction, as SQL Server ends the batch. `BEGIN TRAN` in a trigger is
  dropped.
- **Applied last.** Triggers are applied in the Triggers phase, after the data is copied, so the
  copy does not fire them.
- **Dropped options.** Trigger options (`EXECUTE AS`, `WITH ENCRYPTION`) and
  `NOT FOR REPLICATION` are dropped (Info, S2P0204).

### S2P6001 UpdateColumnAssumed (Warning)

`UPDATE(column)` in an UPDATE trigger. SQL Server answers whether the statement's SET list names
the column. PostgreSQL does not tell a trigger which columns were set, so the translation tests
whether the value changed in some row:

- AFTER trigger: `inserted` and `deleted` are joined on the primary key, so a row whose key
  changed counts as changed. A table without a primary key gives true.
- INSTEAD OF trigger: `NEW.c IS DISTINCT FROM OLD.c`.

The answers differ only for `SET c = c`, where the value does not change. `UPDATE(column)` is
true in an INSERT trigger and false in a DELETE trigger, as in SQL Server.

### S2P6002 ColumnsUpdated (Error)

`COLUMNS_UPDATED()` returns a bitmap of the columns the statement set. PostgreSQL has no
equivalent. Compare `inserted` with `deleted` column by column instead.

### S2P6003 InsteadOfPerRow (Warning)

An INSTEAD OF trigger on a view runs once per row in PostgreSQL, and once per statement in SQL
Server. This is reported when its body is more than one statement. The body sees one row in
`inserted`/`deleted` each time, so code that counts or aggregates them, or that should run once,
needs a look.

### S2P6004 InsteadOfTable (Error)

An INSTEAD OF trigger on a table. PostgreSQL has INSTEAD OF triggers on views only. A `BEFORE …
FOR EACH ROW` trigger that does the work and returns NULL (which skips the row) is the usual
rewrite.

### S2P6005 DdlTrigger (Error)

A DDL trigger (`ON DATABASE`, `ON ALL SERVER`) or a logon trigger. PostgreSQL's event triggers
fire on DDL commands with a different model (and need superuser). Rewrite it as one if it is
still needed.

### S2P6006 TriggerOrder (Info)

A table has several triggers for one event. PostgreSQL fires them in name order. SQL Server fires
them in its own order, which `sp_settriggerorder` sets. Rename them if the order matters.

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

## S2P8xxx — application code (`sql2pg code`)

See [code-conversion.md](code-conversion.md) for what `sql2pg code` changes. These codes point
at a line and column in a C# or VB.NET file.

### S2P8001 SqlExceptionToNpgsql (Warning)

`SqlException` becomes `NpgsqlException`. That is the base of `PostgresException`, so it also
catches network and timeout errors, as `SqlException` did. Errors are identified by `SqlState`, a
five-character SQLSTATE such as `23505`, not by `Number`. Comparisons with known numbers are
rewritten (2627 and 2601 → 23505, 547 → 23503 or 23514, 515 → 23502, 1205 → 40P01, 8152 →
22001, 8134 → 22012, 245 → 22P02, 50000 → P0001). Messages are PostgreSQL's, so code that parses
or shows them may need a look.

### S2P8002 SqlExceptionMember (Error)

A `SqlException` member with no counterpart (`Errors`, `Class`, `LineNumber`, `Procedure`,
`State`, `Server`), or an error number the converter cannot map. For example, -2 is a SqlClient
timeout, which surfaces in Npgsql as an `NpgsqlException` with a `TimeoutException` inside.
Test the SQLSTATE of the same PostgreSQL error (PostgreSQL manual, appendix A) or the exception
type.

### S2P8003 TableValuedParameter (Error)

`SqlDbType.Structured`, a table-valued parameter. The converted routine takes an array of the
table type's composite type (S2P2005), so the code that builds the `DataTable` changes:

1. Write a class with the type's columns, mapping each property to its field (`[PgName("orderreference")]`).
2. Map the class to the type: `dataSourceBuilder.MapComposite<OrderRow>("public.orderlist")`.
3. Pass a `OrderRow[]` as the parameter's value.

From SQL, pass `ARRAY[ROW(…)::public.orderlist, …]`. The converter does not rewrite the
`DataTable` code itself.

### S2P8004 SqlBulkCopy (Error)

`SqlBulkCopy` has no Npgsql class of the same shape. The fast path is binary COPY:
`connection.BeginBinaryImport("COPY t (a, b) FROM STDIN (FORMAT BINARY)")`, then `StartRow` and
`Write` for each row, and `Complete`.

### S2P8005 IntegratedSecurity (Warning)

A connection string asks for Windows authentication (`Integrated Security`,
`Trusted_Connection`). It is dropped. PostgreSQL signs in with a user name and password, or with
Kerberos (GSSAPI) configured on the server. Add `Username` and `Password`, or set up GSSAPI.

### S2P8006 ConnectionStringKeyDropped (Info)

Connection string keys with no Npgsql counterpart are dropped: `MultipleActiveResultSets`,
`TrustServerCertificate`, `MultiSubnetFailover`, `Packet Size`, `Workstation ID`, and the like.
It is a Warning for a named instance (`server\instance`) or LocalDB, which PostgreSQL does not
have: give the PostgreSQL server's port instead.

### S2P8007 SqlTypes (Error)

`System.Data.SqlTypes` values (`SqlString`, `SqlInt32`, …) and the readers that return them
(`GetSqlString`, …) are SqlClient's. Use the plain .NET types, the plain getters and `IsDBNull`.

### S2P8008 SqlClientOnly (Error)

A SqlClient feature with no Npgsql counterpart:

- `SqlDependency`: use LISTEN/NOTIFY with a trigger that calls `pg_notify`.
- `InfoMessage`: PRINT/RAISERROR messages arrive through `NpgsqlConnection.Notice`.
- `ExecuteXmlReader`: select the xml and read it as a string.
- `SqlCredential` and access tokens: use a password provider.
- `SqlDbType.Udt`.
- Connection statistics.

### S2P8010 SqlBuiltAtRunTime (Error)

SQL passed to a command, Dapper or EF Core is built at run time: concatenated with a variable,
`string.Format`, an interpolated string outside EF Core's `FromSql`, or a variable assigned more
than once. Translation needs the text where it is written. Translate the pieces by hand, or build
the SQL from constants and pass values as parameters. Names that must vary (a sort column) need a
fixed list of allowed names in the code.

### S2P8011 IdentityFromLastval (Warning)

`INSERT …; SELECT SCOPE_IDENTITY()` is translated without the schema, so the identity column is
not known. The value comes from `lastval()`, the last value any sequence gave in the session,
which is SQL Server's `@@IDENTITY` rather than `SCOPE_IDENTITY()`: a trigger that inserts
elsewhere changes it. Run `sql2pg code` with `--source`, and the INSERT returns its identity
column (`INSERT … RETURNING`).

### S2P8012 ProceduralSqlInCommand (Error)

A command's SQL declares variables or uses control flow (`DECLARE`, `IF`, `WHILE`, `BEGIN … END`).
A PostgreSQL command is one or more plain statements. Move the logic into a function or
procedure and call it, or into the application code.

### S2P8013 CompatHelperInCode (Warning)

The translated SQL calls `mssql_compat` helpers (for example `charindex`, `datediff`). `sql2pg
schema` creates only the helpers the schema needs. Make sure the database has these too, or
rewrite the expression.

### S2P8020 ProcedureCursors (Error)

The code calls, as a stored procedure, a procedure converted with refcursor result sets
(S2P5021). Its rows do not come back as a reader. Call it inside a transaction, then run
`FETCH ALL FROM <cursor>` for each cursor parameter and read each with its own data reader.
Pass the cursor names (`result_set_1 => 'c1'`) to make that easier.

### S2P8021 ProcedureShapeUnknown (Warning)

The converter cannot tell how this stored procedure call should look after conversion:

- without `--source`, or for a procedure not in the source database, it does not know whether
  the procedure became a function (read with `SELECT * FROM f(…)`) or a procedure (CALL); only
  its name is converted;
- the parameters are not passed where the converter can see them (built in a loop, or in another
  method).

Npgsql names a CALL's arguments after the parameters exactly, so they must be named as the
converted procedure's are.

### S2P8030 DapperContribColumns (Warning)

A Dapper.Contrib entity has property names that differ from the converted column names (with
`NameStyle` Lower, any property with capitals). Contrib's PostgreSQL adapter quotes property
names in `Insert`, `Update` and `Delete` (`"Name"`), so they must equal the column names exactly;
`Get` and `GetAll` do not quote them, and keep working. Contrib has no column mapping, so the
converter cannot fix this by itself. Rename the properties, or write those statements with
Dapper's `Execute` and plain SQL.

### S2P8031 DapperContribAdapter (Info)

The code sets `SqlMapperExtensions.GetDatabaseType` or implements `ISqlAdapter`. With Npgsql,
Contrib picks its PostgreSQL adapter from the connection's type name (`npgsqlconnection`), so an
override that picks SQL Server's dialect must go.

### S2P8040 EfSqlServerOnly (Info)

An EF Core option or fluent call that is SQL Server's was removed or changed:

- removed: `IsClustered`, `IsMemoryOptimized`, `HasFillFactor`, `IsSparse`, `UseCompatibilityLevel`,
  `UseCollation`, the Azure service tier calls, and the like;
- `UseAzureSql` becomes `UseNpgsql`;
- `UseIdentityColumn(seed, increment)` becomes `UseIdentityByDefaultColumn()`. Add
  `HasIdentityOptions` if the seed matters; the converted schema keeps it.

It is a Warning when `HasDefaultValueSql` or `HasComputedColumnSql` could not be translated.

### S2P8041 EfNaming (Info)

EF Core quotes table and column names exactly as its model has them (`"Customers"."Name"`), and
the converted schema's names are lower case (`NameStyle = Lower`). So the converter adds a loop
at the end of `OnModelCreating` (or adds the method):

- every table, column, key, index and foreign key is named in lower case;
- schemas go through the schema map (`dbo` → `public`);
- `DateTime` properties without a column type get `timestamp without time zone`, the type
  datetime and datetime2 are converted to. Npgsql's default for `DateTime` is
  `timestamp with time zone`.

It is a Warning when the loop cannot be added (an expression-bodied OnModelCreating), and under
`NameStyle = SnakeCase`, where the model needs a snake_case convention.

### S2P8042 EfSqlServerFunction (Error)

An `EF.Functions` method that only SQL Server translates (`DateDiffDay` and the other
`DateDiff…` methods, `DateFromParts`, `IsDate`, `IsNumeric`, `DataLength`, full-text `Contains`
and `FreeText`), or a temporal table (`IsTemporal`). Write the query with what Npgsql translates.
For example, `(end.Date - start.Date).Days` for a day difference, or `EF.Functions.ILike` and
`to_tsvector` search. Keep history with a trigger instead of a temporal table.

### S2P8043 EfMigrations (Warning)

An EF Core migration made for SQL Server (`SqlServer:` annotations, SQL Server column types). Do
not convert migrations: regenerate them against PostgreSQL from the converted model
(`dotnet ef migrations add`). The database itself comes from `sql2pg migrate`.

### S2P8044 PackageReplaced (Info)

A package reference changed in a project file or `Directory.Packages.props`:

- `Microsoft.EntityFrameworkCore.SqlServer` → `Npgsql.EntityFrameworkCore.PostgreSQL`, at the
  release for the same EF Core major (8 → 8.0.11, 9 → 9.0.4, 10 → 10.0.3);
- the `.NetTopologySuite` plugin the same way;
- `Microsoft.Data.SqlClient` and `System.Data.SqlClient` → `Npgsql`.

It is a Warning when the matching version is not known (EF Core 7 and earlier): set it by hand.

## S2P9xxx — internal errors

### S2P9001 InternalError (Error)

The converter threw an exception while converting the object. This is a bug in sql2pg, not in
the database. The object's original T-SQL is kept in the output as a comment. Please report it
with the message (no private schema needed: a minimal `CREATE` statement that reproduces it is
best).
