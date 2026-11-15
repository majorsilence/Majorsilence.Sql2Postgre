namespace Majorsilence.Sql2Postgre.Model;

public enum ObjectStatus { Converted, ConvertedWithWarnings, Manual, Skipped }

/// <summary>The script phases, in the order they are applied (see <c>sql2pg schema</c>'s file names).</summary>
/// <remarks>
/// The mssql_compat helpers come before the tables: a default or computed column may call one,
/// and PostgreSQL checks a function exists when the table is created.
/// </remarks>
public enum Phase { Schemas, Types, Compat, Sequences, Tables, Code, Indexes, Constraints, ForeignKeys, Triggers }

/// <summary>One source object's PostgreSQL output.</summary>
/// <param name="TargetName">The PostgreSQL name, schema-qualified and quoted as emitted.</param>
/// <param name="Sql">The statements to run, ending in <c>;\n</c>; may include untranslated comments.</param>
/// <param name="Extensions">Permitted extensions this object's SQL needs.</param>
/// <param name="CompatFunctions">mssql_compat helpers this object's SQL calls.</param>
public sealed record ConvertedObject(ObjectName Source, ObjectKind Kind, string TargetName,
    Phase Phase, string Sql, ObjectStatus Status, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ObjectName> DependsOn)
{
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public IReadOnlyList<string> CompatFunctions { get; init; } = [];

    /// <summary>The status a set of diagnostics implies: any Error is Manual, any Warning is ConvertedWithWarnings.</summary>
    public static ObjectStatus StatusFor(IEnumerable<Diagnostic> diagnostics)
    {
        var worst = diagnostics.Select(d => d.Severity).DefaultIfEmpty(Severity.Info).Max();
        return worst switch
        {
            Severity.Error => ObjectStatus.Manual,
            Severity.Warning => ObjectStatus.ConvertedWithWarnings,
            _ => ObjectStatus.Converted,
        };
    }
}

/// <param name="Diagnostics">Diagnostics not tied to a single converted object.</param>
public sealed record ConversionResult(IReadOnlyList<ConvertedObject> Objects, IReadOnlyList<Diagnostic> Diagnostics);
