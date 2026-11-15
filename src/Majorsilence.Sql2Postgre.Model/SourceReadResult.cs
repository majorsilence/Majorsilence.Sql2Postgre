namespace Majorsilence.Sql2Postgre.Model;

/// <summary>
/// What a reader found, and what it could not read (encrypted modules, CLR objects, parse
/// errors). An addition to the plan's contracts: <see cref="SourceDatabase"/> has nowhere to
/// carry the reader's own diagnostics.
/// </summary>
public sealed record SourceReadResult(SourceDatabase Database, IReadOnlyList<Diagnostic> Diagnostics);
