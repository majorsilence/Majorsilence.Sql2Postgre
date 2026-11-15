namespace Majorsilence.Sql2Postgre.Model;

/// <summary>How much a diagnostic matters. Any <see cref="Error"/> makes its object <see cref="ObjectStatus.Manual"/>.</summary>
public enum Severity { Info, Warning, Error }

/// <summary>Where in an object's T-SQL text a diagnostic points (1-based line and column).</summary>
public sealed record SourceSpan(int Line, int Column, int Length);

/// <summary>Something the converter could not do, did differently, or wants a person to know.</summary>
/// <param name="Code">A <see cref="DiagnosticCodes"/> constant: <c>S2P</c> and four digits.</param>
public sealed record Diagnostic(string Code, Severity Severity, ObjectName? Object, string Message, SourceSpan? Span = null)
{
    public override string ToString() =>
        $"{Code} {Severity}{(Object is null ? "" : $" {Object}")}: {Message}";
}
