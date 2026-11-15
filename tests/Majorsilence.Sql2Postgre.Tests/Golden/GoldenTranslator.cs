namespace Majorsilence.Sql2Postgre.Tests.Golden;

public sealed record GoldenDiagnostic(string Code, string Severity);

public sealed record GoldenResult(string Sql, IReadOnlyList<GoldenDiagnostic> Diagnostics);

/// <summary>
/// The translator the golden cases run through. A placeholder that echoes its input until the
/// expression translator exists (WP 2.2 replaces the body); the harness itself does not change.
/// </summary>
public static class GoldenTranslator
{
    public static GoldenResult Translate(string tsql, string? optionsJson) => new(tsql, []);
}
