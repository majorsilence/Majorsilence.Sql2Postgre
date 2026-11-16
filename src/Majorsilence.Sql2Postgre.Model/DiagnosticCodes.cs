namespace Majorsilence.Sql2Postgre.Model;

/// <summary>
/// Every diagnostic code, grouped by area. Each also has a section in <c>docs/diagnostics.md</c>
/// saying what it means and what a person should do about it. Codes are never reused.
/// </summary>
/// <remarks>
/// Ranges: 0xxx input, reading and parsing; 1xxx types, names and collations; 2xxx schema objects;
/// 3xxx expressions and built-in functions; 4xxx queries and DML; 5xxx procedural code;
/// 6xxx triggers; 7xxx data copy and verification; 9xxx internal errors.
/// </remarks>
public static class DiagnosticCodes
{
    /// <summary>A T-SQL batch did not parse. The batch is skipped; the reader continues with the next.</summary>
    public const string ParseError = "S2P0001";

    /// <summary>The module was created <c>WITH ENCRYPTION</c>; SQL Server does not reveal its text.</summary>
    public const string EncryptedModule = "S2P0101";

    /// <summary>A CLR function, procedure, aggregate or trigger: .NET code, not T-SQL.</summary>
    public const string ClrObject = "S2P0102";

    /// <summary>Views, functions, procedures and triggers are listed but not converted until Stage 2/3.</summary>
    public const string NotYetSupported = "S2P0201";

    /// <summary>The object uses another that is not converted yet (a routine, before Stage 3) or needs a person.</summary>
    public const string DependsOnUnconverted = "S2P0203";

    /// <summary>USE or a session SET option in a script is dropped.</summary>
    public const string SessionStatementDropped = "S2P0204";

    /// <summary>DDL in an ad-hoc script: schema objects are converted by sql2pg schema.</summary>
    public const string DdlInScript = "S2P0205";

    /// <summary>Procedural T-SQL not translated yet (EXEC, TRY/CATCH, cursors, temp tables, transactions, …).</summary>
    public const string ProceduralStatement = "S2P0206";

    /// <summary>Objects depend on each other in a cycle; they are emitted in name order.</summary>
    public const string DependencyCycle = "S2P0202";

    /// <summary>Code refers to an object in another database or on a linked server.</summary>
    public const string ExternalReference = "S2P0301";

    /// <summary>A name is longer than PostgreSQL's 63-byte limit and was shortened, with a hash to keep it unique.</summary>
    public const string IdentifierTruncated = "S2P1001";

    /// <summary>Two names map to the same PostgreSQL identifier; the later one gets a numeric suffix.</summary>
    public const string IdentifierCollision = "S2P1002";

    /// <summary>SQL Server keeps 7 fractional-second digits here; PostgreSQL keeps 6, so values are rounded.</summary>
    public const string FractionalSecondsReduced = "S2P1101";

    /// <summary>datetimeoffset keeps each value's offset; timestamptz keeps only the instant.</summary>
    public const string OffsetNotPreserved = "S2P1102";

    /// <summary>rowversion becomes an ordinary bytea column; SQL Server no longer maintains it.</summary>
    public const string RowversionSemantics = "S2P1103";

    /// <summary>sql_variant values are kept as text; their original types are lost.</summary>
    public const string SqlVariantAsText = "S2P1104";

    /// <summary>hierarchyid values are kept as their string form; its methods need rewriting.</summary>
    public const string HierarchyIdAsText = "S2P1105";

    /// <summary>A spatial column is kept as WKB in bytea because postgis is disabled.</summary>
    public const string SpatialAsWkb = "S2P1106";

    /// <summary>A vector column is kept as real[] because the vector extension is disabled.</summary>
    public const string VectorAsArray = "S2P1107";

    /// <summary>No PostgreSQL type is known for the SQL Server type; it is written as text.</summary>
    public const string UnknownType = "S2P1199";

    /// <summary>A text column uses a case-insensitive collation; PostgreSQL will compare it case-sensitively.</summary>
    public const string CaseInsensitiveCollation = "S2P1301";

    /// <summary>LIKE is not supported on a nondeterministic collation before PostgreSQL 18.</summary>
    public const string LikeOnNondeterministic = "S2P1302";

    /// <summary>The object uses a permitted extension, which the target database must have available.</summary>
    public const string ExtensionRequired = "S2P1401";

    /// <summary>A non-persisted computed column becomes a STORED generated column (VIRTUAL needs PostgreSQL 18).</summary>
    public const string GeneratedColumnStored = "S2P2001";

    /// <summary>Clustered indexes and primary keys become ordinary B-tree indexes; PostgreSQL does not keep rows in index order.</summary>
    public const string ClusteredIgnored = "S2P2002";

    /// <summary>A columnstore, XML, full-text, hash or (without postgis) spatial index has no PostgreSQL equivalent and is not created.</summary>
    public const string IndexTypeUnsupported = "S2P2003";

    /// <summary>A constraint SQL Server does not trust is created NOT VALID: existing rows are not checked.</summary>
    public const string ConstraintNotValid = "S2P2004";

    /// <summary>A table type becomes a composite type; table-valued parameters need manual review.</summary>
    public const string TableTypeAsComposite = "S2P2005";

    /// <summary>A sequence's type has no PostgreSQL sequence equivalent and becomes bigint.</summary>
    public const string SequenceTypeChanged = "S2P2006";

    /// <summary>A computed column read from a script has a type that cannot be inferred; a type override is needed.</summary>
    public const string ComputedColumnTypeUnknown = "S2P2007";

    /// <summary>A computed column read from a script has its type inferred from its expression.</summary>
    public const string ComputedColumnTypeInferred = "S2P2010";

    /// <summary>An identity column on decimal/numeric becomes an integer type, as PostgreSQL identity requires.</summary>
    public const string IdentityTypeChanged = "S2P2008";

    /// <summary>A computed column's expression is not immutable, which a PostgreSQL generated column requires.</summary>
    public const string GeneratedNotImmutable = "S2P2009";

    /// <summary>ROWGUIDCOL has no meaning in PostgreSQL and is dropped.</summary>
    public const string RowGuidColIgnored = "S2P2011";

    /// <summary>SPARSE has no meaning in PostgreSQL (NULLs take no space anyway) and is dropped.</summary>
    public const string SparseIgnored = "S2P2012";

    /// <summary>A disabled constraint is created NOT VALID, but PostgreSQL will enforce it for new rows.</summary>
    public const string DisabledConstraintEnforced = "S2P2013";

    /// <summary>A key, index or constraint uses a column that was left out of the table (it could not be translated).</summary>
    public const string UsesUntranslatedColumn = "S2P2014";

    /// <summary>An indexed view becomes a materialized view, which must be refreshed (SQL Server kept it current).</summary>
    public const string MaterializedViewRefresh = "S2P2015";

    /// <summary>WITH SCHEMABINDING (and VIEW_METADATA) have no PostgreSQL counterpart and are dropped.</summary>
    public const string ViewOptionDropped = "S2P2016";

    /// <summary>An expression uses T-SQL the translator does not handle yet; it is kept as a comment.</summary>
    public const string UnsupportedExpression = "S2P3001";

    /// <summary>NEWSEQUENTIALID() becomes a random UUID (before PostgreSQL 18), so values are not ordered.</summary>
    public const string NotSequential = "S2P3002";

    /// <summary>CONVERT with a style the translator does not know (or a non-literal style).</summary>
    public const string ConvertStyleUnsupported = "S2P3003";

    /// <summary>FORMAT with a format or culture the translator does not know.</summary>
    public const string FormatUnsupported = "S2P3004";

    /// <summary>DATEPART(weekday/week) depends on SET DATEFIRST; the default (7) is assumed.</summary>
    public const string DateFirstAssumed = "S2P3005";

    /// <summary>@@ROWCOUNT, SCOPE_IDENTITY(), ERROR_*() and the like belong to procedural code (Stage 3).</summary>
    public const string ProceduralGlobal = "S2P3006";

    /// <summary>A JSON path that is not a simple literal $.a.b[n] path.</summary>
    public const string JsonPathUnsupported = "S2P3007";

    /// <summary>HASHBYTES on nvarchar hashes UTF-16 bytes in SQL Server, UTF-8 in the translation.</summary>
    public const string HashEncodingDiffers = "S2P3008";

    /// <summary>The construct needs a permitted extension that the options disable.</summary>
    public const string ExtensionDisabled = "S2P3009";

    /// <summary>+ on operands of unknown type is kept as addition; if they are strings it must be ||.</summary>
    public const string AmbiguousPlus = "S2P3010";

    /// <summary>ROUND on a float rounds the binary value in SQL Server and the decimal value in PostgreSQL.</summary>
    public const string FloatRounding = "S2P3012";

    /// <summary>NUL characters were removed from text values; PostgreSQL text cannot hold them.</summary>
    public const string NulCharacterRemoved = "S2P7001";

    /// <summary>Spatial values had Z or M coordinates, which the copy drops.</summary>
    public const string SpatialZmDropped = "S2P7002";

    /// <summary>A table's data (or a sequence's position) could not be copied.</summary>
    public const string TableCopyFailed = "S2P7003";

    /// <summary>A table has a different number of rows in PostgreSQL than in SQL Server.</summary>
    public const string RowCountMismatch = "S2P7101";

    /// <summary>A table has the same number of rows on both sides but different values.</summary>
    public const string ChecksumMismatch = "S2P7102";

    /// <summary>A column reference could not be resolved; it is written by its name without its type.</summary>
    public const string UnresolvedColumn = "S2P3013";

    /// <summary>DATALENGTH counts UTF-16 bytes for nvarchar in SQL Server, UTF-8 bytes in PostgreSQL.</summary>
    public const string ByteLengthDiffers = "S2P3014";

    /// <summary>A COLLATE clause is dropped.</summary>
    public const string CollationIgnored = "S2P3015";

    /// <summary>The translation needs an mssql_compat helper and the compat schema is disabled.</summary>
    public const string CompatDisabled = "S2P3020";

    /// <summary>A spatial method (STDistance, …), which needs the PostGIS mapping of Stage 4.</summary>
    public const string SpatialMethod = "S2P3030";

    /// <summary>TOP … PERCENT counts the rows first: the query runs twice.</summary>
    public const string TopPercent = "S2P4001";

    /// <summary>A table, join or query hint is dropped (PostgreSQL's planner has no hints).</summary>
    public const string HintDropped = "S2P4002";

    /// <summary>INSERT … EXEC needs the procedure translated (Stage 3).</summary>
    public const string InsertExec = "S2P4003";

    /// <summary>SET IDENTITY_INSERT is dropped (identities accept explicit values); the sequence is moved past them.</summary>
    public const string IdentityInsert = "S2P4004";

    /// <summary>UPDATE … SET @v = col = expr (a variable assigned row by row).</summary>
    public const string QuirkyUpdate = "S2P4005";

    /// <summary>UPDATE or DELETE whose target is inside an outer join of its FROM clause.</summary>
    public const string UpdateFromOuterJoin = "S2P4008";

    /// <summary>UPDATE or DELETE with TOP.</summary>
    public const string DmlTop = "S2P4009";

    /// <summary>FOR XML other than PATH('') concatenation.</summary>
    public const string ForXmlUnsupported = "S2P4010";

    /// <summary>FOR JSON beyond PATH (or AUTO over one table) with named columns.</summary>
    public const string ForJsonUnsupported = "S2P4011";

    /// <summary>TOP 100 PERCENT … ORDER BY in a view is dropped (SQL Server ignores that order too).</summary>
    public const string ViewOrderIgnored = "S2P4012";

    /// <summary>OUTPUT … INTO a table needs procedural code (Stage 3).</summary>
    public const string OutputInto = "S2P4013";

    // ---- 5xxx procedural code ----

    /// <summary>A variable declared twice with different types (PostgreSQL has one declaration per body).</summary>
    public const string VariableRedeclared = "S2P5001";

    /// <summary>SELECT assigns variables from a query that may return several rows and has no ORDER BY: which row's values remain is arbitrary.</summary>
    public const string SelectAssignmentOrder = "S2P5002";

    /// <summary>Without XACT_ABORT, SQL Server continues after most statement errors; PostgreSQL stops at the first.</summary>
    public const string ErrorStopsExecution = "S2P5003";

    /// <summary>@@ERROR outside a CATCH block is translated as 0 (an error has already ended the routine in PostgreSQL).</summary>
    public const string ErrorGlobal = "S2P5004";

    /// <summary>GOTO and labels have no PL/pgSQL equivalent.</summary>
    public const string GotoLabel = "S2P5005";

    /// <summary>SELECT @v = expression-using-@v over a query with aggregates, grouping or subqueries (row-by-row accumulation that cannot be rewritten).</summary>
    public const string SelectAssignmentAccumulates = "S2P5006";

    /// <summary>RAISERROR with severity 11–18 outside TRY: SQL Server continues with the next statement, PostgreSQL ends the routine.</summary>
    public const string RaiserrorStops = "S2P5007";

    /// <summary>RAISERROR with a message number from sys.messages, or a non-literal message with arguments.</summary>
    public const string RaiserrorUntranslatable = "S2P5008";

    /// <summary>A statement returning rows inside a script batch that became a DO block, which cannot return rows.</summary>
    public const string ResultSetInBlock = "S2P5009";

    /// <summary>An inline table-valued function's result column has no known type, so RETURNS TABLE cannot be written.</summary>
    public const string FunctionColumnTypeUnknown = "S2P5010";

    /// <summary>A parameter named like one of the function's result columns is renamed p_name.</summary>
    public const string ParameterRenamed = "S2P5012";

    /// <summary>A parameter after one with a default gets DEFAULT NULL (PostgreSQL requires it).</summary>
    public const string ParameterDefaultAdded = "S2P5020";

    /// <summary>RAISERROR format flags, width, precision or %x/%o: the arguments are written in their plain text form.</summary>
    public const string RaiserrorFormat = "S2P5011";

    /// <summary>The converter threw while converting an object: a bug. The object is left for a person.</summary>
    public const string InternalError = "S2P9001";
}
