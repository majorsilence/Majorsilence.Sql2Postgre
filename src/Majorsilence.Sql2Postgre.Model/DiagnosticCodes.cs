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
    /// <summary>The converter threw while converting an object: a bug. The object is left for a person.</summary>
    public const string InternalError = "S2P9001";
}
