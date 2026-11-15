using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;

namespace Majorsilence.Sql2Postgre.Translation.Schema;

/// <summary>
/// One source object translated to PostgreSQL statements. The Postgres layer writes the
/// statements as text to make the <see cref="ConvertedObject"/> (the translator does not write
/// SQL text itself: the writer lives in the Postgres project, which depends on this one).
/// </summary>
public sealed record TranslatedObject(ObjectName Source, ObjectKind Kind, string TargetName, Phase Phase,
    IReadOnlyList<PgStatement> Statements, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<ObjectName> DependsOn)
{
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>The mssql_compat helpers the statements call.</summary>
    public IReadOnlyList<string> CompatFunctions { get; init; } = [];

    /// <summary>Set for objects deliberately not converted (indexes with no equivalent, modules before Stage 2).</summary>
    public bool Skipped { get; init; }

    public ObjectStatus Status => Skipped ? ObjectStatus.Skipped : ConvertedObject.StatusFor(Diagnostics);
}
