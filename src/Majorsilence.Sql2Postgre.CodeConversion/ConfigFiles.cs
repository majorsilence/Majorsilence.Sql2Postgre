using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Majorsilence.Sql2Postgre.CodeConversion.Rules;
using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis.Text;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>
/// Configuration files: <c>appsettings*.json</c> (comments allowed) and <c>web.config</c> /
/// <c>app.config</c> (and their transforms). Every SQL Server connection string in them becomes an
/// Npgsql one, with the conversion the code rules use, and a <c>providerName</c> of SqlClient
/// becomes <c>Npgsql</c>. The text is edited in place, so formatting and comments stay. Diagnostics
/// name the setting, never its value, which may hold a password. Database projects (<c>.sqlproj</c>,
/// <c>.dbproj</c>) are left alone.
/// </summary>
public static partial class ConfigFiles
{
    public static bool Is(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        return name.StartsWith("appsettings", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal)
               || name.EndsWith(".config", StringComparison.Ordinal) && (name.StartsWith("web.", StringComparison.Ordinal) || name.StartsWith("app.", StringComparison.Ordinal))
               || IsDatabaseProject(path);
    }

    private static bool IsDatabaseProject(string path) => Path.GetExtension(path).ToLowerInvariant() is ".sqlproj" or ".dbproj";

    public static CodeFileResult Convert(string relativePath, string text)
    {
        var edits = new List<CodeEdit>();
        var diagnostics = new List<Diagnostic>();
        var lines = SourceText.From(text).Lines;
        void Report(string code, Severity severity, int at, int length, string message)
        {
            var position = lines.GetLinePosition(at);
            diagnostics.Add(new Diagnostic(code, severity, new ObjectName("", relativePath), message,
                new SourceSpan(position.Line + 1, position.Character + 1, length)));
        }
        void ConnectionString(string setting, string value, int at, int length, Func<string, string> write)
        {
            var notes = new List<ConnectionStringNote>();
            if (ConnectionStrings.Convert(value, notes) is not { } converted)
                return;
            edits.Add(new CodeEdit(new TextSpan(at, length), write(converted), $"connection string {setting}"));
            Report(DiagnosticCodes.ConfigConnectionString, Severity.Info, at, length, $"The connection string {setting} is now an Npgsql connection string.");
            foreach (var n in notes)
                Report(n.Code, n.Severity, at, length, $"{setting}: {n.Message}");
        }

        string language = "config";
        if (IsDatabaseProject(relativePath))
        {
            Report(DiagnosticCodes.DatabaseProjectSkipped, Severity.Info, 0, 0,
                "A SQL Server database project is left as it is: its schema is converted by sql2pg schema (from the database or its scripts), not as a project.");
            return new CodeFileResult(relativePath, "sqlproj", text, [], diagnostics);
        }
        if (relativePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            language = "json";
            foreach (var s in JsonStrings(text).Where(s => !s.IsKey && ConnectionStrings.Looks(s.Value)))
                ConnectionString(s.Path, s.Value, s.Start, s.Length, v => "\"" + JsonEscape(v) + "\"");
        }
        else
        {
            foreach (Match element in AddElement().Matches(text))
            {
                string tag = element.Value;
                var name = Attribute(tag, "name") ?? Attribute(tag, "key");
                string setting = name is null ? "(unnamed)" : $"'{WebUtility.HtmlDecode(name.Value)}'";
                foreach (var attributeName in new[] { "connectionString", "value" })
                {
                    if (Attribute(tag, attributeName) is not { } attribute)
                        continue;
                    string value = WebUtility.HtmlDecode(attribute.Value);
                    if (ConnectionStrings.Looks(value))
                        ConnectionString(setting, value, element.Index + attribute.Index, attribute.Length, XmlEscape);
                }
                if (Attribute(tag, "providerName") is { } provider
                    && provider.Value is "System.Data.SqlClient" or "Microsoft.Data.SqlClient")
                    edits.Add(new CodeEdit(new TextSpan(element.Index + provider.Index, provider.Length), "Npgsql", $"providerName of {setting}"));
            }
        }
        try
        {
            return new CodeFileResult(relativePath, language, EditEngine.Apply(text, edits), edits, diagnostics);
        }
        catch (OverlappingEditsException ex)
        {
            diagnostics.Add(new Diagnostic(DiagnosticCodes.InternalError, Severity.Error, new ObjectName("", relativePath),
                $"The converter failed on this file and left it unchanged: {ex.Message}"));
            return new CodeFileResult(relativePath, language, text, [], diagnostics);
        }
    }

    /// <summary>A JSON string literal: its unescaped value, where its quotes start and how long it is, whether it is a key, and its setting's path (<c>ConnectionStrings:Default</c>).</summary>
    private sealed record JsonString(string Value, int Start, int Length, bool IsKey, string Path);

    /// <summary>
    /// The string literals of a JSON document with comments (as appsettings files allow): a small
    /// scanner that tracks the object keys above each value, so a setting is named by its path.
    /// </summary>
    private static List<JsonString> JsonStrings(string text)
    {
        var result = new List<JsonString>();
        var path = new List<string?>();
        string? pendingKey = null;
        int i = 0;
        while (i < text.Length)
        {
            char ch = text[i];
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }
            if (ch == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                continue;
            }
            if (ch == '"')
            {
                int start = i;
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length)
                    {
                        char e = text[i + 1];
                        if (e == 'u' && i + 5 < text.Length && int.TryParse(text.AsSpan(i + 2, 4), System.Globalization.NumberStyles.HexNumber, null, out int code))
                        {
                            sb.Append((char)code);
                            i += 6;
                            continue;
                        }
                        sb.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => e });
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i]);
                    i++;
                }
                i++;
                int after = i;
                while (after < text.Length && char.IsWhiteSpace(text[after]))
                    after++;
                bool isKey = after < text.Length && text[after] == ':';
                string value = sb.ToString();
                if (isKey)
                    pendingKey = value;
                string settingPath = string.Join(':', path.OfType<string>().Append(isKey ? value : pendingKey ?? "").Where(p => p.Length > 0));
                result.Add(new JsonString(value, start, i - start, isKey, settingPath));
                if (!isKey)
                    pendingKey = null;
                continue;
            }
            switch (ch)
            {
                case '{' or '[':
                    path.Add(pendingKey);
                    pendingKey = null;
                    break;
                case '}' or ']':
                    if (path.Count > 0)
                        path.RemoveAt(path.Count - 1);
                    pendingKey = null;
                    break;
                case ',':
                    pendingKey = null;
                    break;
            }
            i++;
        }
        return result;
    }

    private static string JsonEscape(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        return sb.ToString();
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("'", "&apos;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>An attribute's value (between its quotes) inside an element's text, with its offset in that text.</summary>
    private static Group? Attribute(string element, string name)
    {
        var m = Regex.Match(element, $"\\b{name}\\s*=\\s*(\"(?<v>[^\"]*)\"|'(?<v>[^']*)')", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"] : null;
    }

    [GeneratedRegex("<add\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AddElement();
}
