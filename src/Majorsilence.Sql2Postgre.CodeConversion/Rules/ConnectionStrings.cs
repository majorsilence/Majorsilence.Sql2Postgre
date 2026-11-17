using System.Text;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.CodeConversion.Rules;

/// <summary>A note about a converted connection string.</summary>
internal sealed record ConnectionStringNote(string Code, Severity Severity, string Message);

/// <summary>
/// SQL Server connection strings to Npgsql's: keys renamed (<c>Server</c> → <c>Host</c> and
/// <c>Port</c>, <c>Initial Catalog</c> → <c>Database</c>, <c>User Id</c> → <c>Username</c>),
/// encryption to <c>SSL Mode</c>, pooling and timeouts kept, SQL Server-only keys dropped and
/// reported.
/// </summary>
internal static class ConnectionStrings
{
    private static readonly string[] ServerKeys = ["server", "data source", "address", "addr", "network address"];

    /// <summary>Whether a string is a SQL Server connection string: a server key and a database or credential key.</summary>
    public static bool Looks(string value)
    {
        var parts = Parse(value);
        return parts is not null
               && parts.Any(p => ServerKeys.Contains(p.Key))
               && parts.Any(p => p.Key is "database" or "initial catalog" or "user id" or "uid" or "user" or "integrated security" or "trusted_connection");
    }

    /// <summary>The Npgsql connection string, or null when the value is not one to convert.</summary>
    public static string? Convert(string value, List<ConnectionStringNote> notes)
    {
        var parts = Parse(value);
        if (parts is null || !Looks(value))
            return null;
        var result = new List<(string Key, string Value)>();
        var dropped = new List<string>();
        foreach (var (key, raw, original) in parts)
        {
            string v = raw.Trim();
            switch (key)
            {
                case var k when ServerKeys.Contains(k):
                    foreach (var pair in Server(v, notes))
                        result.Add(pair);
                    break;
                case "database" or "initial catalog":
                    result.Add(("Database", v));
                    break;
                case "user id" or "uid" or "user":
                    result.Add(("Username", v));
                    break;
                case "password" or "pwd":
                    result.Add(("Password", v));
                    break;
                case "integrated security" or "trusted_connection":
                    if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v.Equals("sspi", StringComparison.OrdinalIgnoreCase) || v.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        notes.Add(new ConnectionStringNote(DiagnosticCodes.IntegratedSecurity, Severity.Warning,
                            "Integrated Security is dropped: PostgreSQL signs in with a user name and password, or Kerberos (GSSAPI) configured on the server. Add Username and Password (or set up GSSAPI)."));
                    break;
                case "encrypt":
                    if (v.ToLowerInvariant() is "true" or "yes" or "mandatory" or "strict")
                        result.Add(("SSL Mode", v.Equals("strict", StringComparison.OrdinalIgnoreCase) ? "VerifyFull" : "Require"));
                    break;
                case "trustservercertificate":
                    dropped.Add(original);
                    break;
                case "connect timeout" or "connection timeout" or "timeout":
                    result.Add(("Timeout", v));
                    break;
                case "command timeout":
                    result.Add(("Command Timeout", v));
                    break;
                case "pooling":
                    result.Add(("Pooling", v));
                    break;
                case "max pool size":
                    result.Add(("Maximum Pool Size", v));
                    break;
                case "min pool size":
                    result.Add(("Minimum Pool Size", v));
                    break;
                case "application name":
                    result.Add(("Application Name", v));
                    break;
                case "persist security info":
                    result.Add(("Persist Security Info", v));
                    break;
                default:
                    dropped.Add(original);
                    break;
            }
        }
        if (dropped.Count > 0)
            notes.Add(new ConnectionStringNote(DiagnosticCodes.ConnectionStringKeyDropped, Severity.Info,
                $"Connection string keys with no Npgsql counterpart are dropped: {string.Join(", ", dropped)}."));
        var sb = new StringBuilder();
        foreach (var (k, v) in result)
            sb.Append(k).Append('=').Append(Quote(v)).Append(';');
        return sb.ToString().TrimEnd(';');
    }

    /// <summary>Host and port from <c>tcp:host,port</c>, <c>host\instance</c>, <c>.</c> or <c>(local)</c>.</summary>
    private static IEnumerable<(string, string)> Server(string server, List<ConnectionStringNote> notes)
    {
        string s = server;
        if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
            s = s[4..];
        string? port = null;
        int comma = s.IndexOf(',');
        if (comma >= 0)
        {
            port = s[(comma + 1)..].Trim();
            s = s[..comma].Trim();
        }
        int slash = s.IndexOf('\\');
        if (slash >= 0)
        {
            if (s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
                notes.Add(new ConnectionStringNote(DiagnosticCodes.ConnectionStringKeyDropped, Severity.Warning,
                    $"'{server}' is SQL Server LocalDB, which has no PostgreSQL counterpart: Host is localhost; run a local PostgreSQL."));
            else
                notes.Add(new ConnectionStringNote(DiagnosticCodes.ConnectionStringKeyDropped, Severity.Warning,
                    $"'{server}' names a SQL Server instance; PostgreSQL has no named instances: give the port of the PostgreSQL server."));
            s = s[..slash];
        }
        if (s is "." or "(local)" or "" || s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
            s = "localhost";
        yield return ("Host", s);
        if (port is not null)
            yield return ("Port", port);
    }

    private static string Quote(string value) =>
        value.IndexOfAny([';', '=', '\'', '"']) >= 0 || value != value.Trim() ? "'" + value.Replace("'", "''") + "'" : value;

    /// <summary>The key/value pairs (keys lower case, values unquoted) with each pair's original text, or null when it does not parse.</summary>
    private static List<(string Key, string Value, string Original)>? Parse(string value)
    {
        var result = new List<(string, string, string)>();
        int i = 0;
        while (i < value.Length)
        {
            while (i < value.Length && (value[i] == ';' || char.IsWhiteSpace(value[i])))
                i++;
            if (i >= value.Length)
                break;
            int eq = value.IndexOf('=', i);
            if (eq < 0)
                return null;
            string original = value[i..eq].Trim();
            string key = original.ToLowerInvariant();
            if (key.Length == 0)
                return null;
            int j = eq + 1;
            while (j < value.Length && value[j] == ' ')
                j++;
            string v;
            if (j < value.Length && value[j] is '\'' or '"')
            {
                char q = value[j];
                var sb = new StringBuilder();
                int k = j + 1;
                for (; k < value.Length; k++)
                {
                    if (value[k] == q && k + 1 < value.Length && value[k + 1] == q)
                    {
                        sb.Append(q);
                        k++;
                    }
                    else if (value[k] == q)
                        break;
                    else
                        sb.Append(value[k]);
                }
                v = sb.ToString();
                i = value.IndexOf(';', Math.Min(k + 1, value.Length)) is var semi and >= 0 ? semi + 1 : value.Length;
            }
            else
            {
                int semi = value.IndexOf(';', j);
                v = semi < 0 ? value[j..] : value[j..semi];
                i = semi < 0 ? value.Length : semi + 1;
            }
            result.Add((key, v, original));
        }
        return result;
    }
}
