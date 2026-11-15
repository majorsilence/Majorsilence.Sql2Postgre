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

    /// <summary>An expression uses T-SQL the translator does not handle yet; it is kept as a comment.</summary>
    public const string UnsupportedExpression = "S2P3001";

    /// <summary>NEWSEQUENTIALID() becomes a random UUID (before PostgreSQL 18), so values are not ordered.</summary>
    public const string NotSequential = "S2P3002";

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

    /// <summary>The converter threw while converting an object: a bug. The object is left for a person.</summary>
    public const string InternalError = "S2P9001";
}
