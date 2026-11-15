using System.Text.RegularExpressions;

namespace Majorsilence.Sql2Postgre.SqlServer;

/// <summary>
/// Splits a script into batches on <c>GO</c> lines, as <c>sqlcmd</c> and SSMS do: <c>GO</c> alone
/// on a line, in any case, optionally followed by a comment (<c>GO 5</c>, a repeat count, is not
/// supported). It does not look inside strings or block comments, so it is for scripts this repo
/// writes; the script reader (WP 1.3) splits batches with ScriptDom instead. The blank lines
/// around each batch are removed so the text SQL Server stores starts at the first statement.
/// </summary>
public static partial class BatchSplitter
{
    public static IReadOnlyList<string> Split(string script) =>
        GoLine().Split(script.Replace("\r\n", "\n"))
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim('\n'))
            .ToList();

    [GeneratedRegex(@"^[ \t]*GO[ \t]*(?:--[^\n]*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
