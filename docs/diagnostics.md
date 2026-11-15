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

## S2P9xxx — internal errors

### S2P9001 InternalError (Error)

The converter threw an exception while converting the object. This is a bug in sql2pg, not in
the database. The object's original T-SQL is kept in the output as a comment. Please report it
with the message (no private schema needed: a minimal `CREATE` statement that reproduces it is
best).
