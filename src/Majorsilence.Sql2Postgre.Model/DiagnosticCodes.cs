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

    /// <summary>A name is longer than PostgreSQL's 63-byte limit and was shortened, with a hash to keep it unique.</summary>
    public const string IdentifierTruncated = "S2P1001";

    /// <summary>Two names map to the same PostgreSQL identifier; the later one gets a numeric suffix.</summary>
    public const string IdentifierCollision = "S2P1002";

    /// <summary>The converter threw while converting an object: a bug. The object is left for a person.</summary>
    public const string InternalError = "S2P9001";
}
