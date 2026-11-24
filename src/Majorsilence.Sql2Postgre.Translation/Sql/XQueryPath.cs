using System.Text;
using System.Text.RegularExpressions;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>An XPath 1.0 expression PostgreSQL can evaluate, with the namespaces its prefixes stand for.</summary>
public sealed record XPathExpression(string Path, IReadOnlyList<(string Prefix, string Uri)> Namespaces);

/// <summary>
/// Turns the XQuery a SQL Server xml method takes into XPath 1.0 for PostgreSQL (libxml2):
/// <list type="bullet">
/// <item>The prolog's <c>declare namespace p="uri";</c> become the namespace list.</item>
/// <item>A <c>declare default element namespace "uri";</c> has no XPath 1.0 counterpart (and
/// PostgreSQL rejects a DEFAULT namespace), so every unprefixed element name gets a prefix of its
/// own bound to that uri.</item>
/// <item><c>f(…)[1]</c> after a function returning a string or number (<c>string(.)[1]</c>) is
/// XQuery's singleton marker; XPath 1.0 has no predicate there, and the value is one already.</item>
/// <item>XQuery beyond XPath 1.0 (FLWOR, variables, <c>sql:column</c>, <c>data()</c>,
/// constructors, conditionals) is rejected with <see cref="XQueryNotXPathException"/>.</item>
/// </list>
/// </summary>
public static partial class XQueryPath
{
    /// <summary>The XPath 1.0 node type tests and the functions that return a node-set (a predicate may follow them).</summary>
    private static readonly HashSet<string> NodeFunctions = new(StringComparer.Ordinal) { "text", "node", "comment", "processing-instruction", "id" };

    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal) { "and", "or", "div", "mod" };

    /// <summary>XQuery functions and keywords XPath 1.0 does not have.</summary>
    private static readonly HashSet<string> XQueryOnly = new(StringComparer.Ordinal)
    {
        "data", "distinct-values", "empty", "exists", "deep-equal", "fn:data", "sql:column", "sql:variable",
        "xs:string", "xs:decimal", "xs:integer", "xs:date", "xs:dateTime", "xs:boolean", "xs:double", "xs:float",
    };

    /// <summary>
    /// The prolog (its text, as <see cref="Convert"/> reads it before a path), the default element
    /// namespace and prefixes it declares, and the expression after it.
    /// </summary>
    public static (string Prolog, string? DefaultNamespace, IReadOnlyList<(string Prefix, string Uri)> Namespaces, string Body) SplitProlog(string xquery)
    {
        var namespaces = new List<(string Prefix, string Uri)>();
        string? defaultNamespace = null;
        string body = xquery;
        while (Prolog().Match(body) is { Success: true } m)
        {
            if (m.Groups["default"].Success)
                defaultNamespace = m.Groups["uri"].Value;
            else
            {
                namespaces.RemoveAll(n => n.Prefix == m.Groups["prefix"].Value);
                namespaces.Add((m.Groups["prefix"].Value, m.Groups["uri"].Value));
            }
            body = body[m.Length..];
        }
        return (xquery[..^body.Length], defaultNamespace, namespaces, body.Trim());
    }

    public static XPathExpression Convert(string xquery, IReadOnlyList<(string Prefix, string Uri)> outerNamespaces)
    {
        var namespaces = new List<(string Prefix, string Uri)>(outerNamespaces);
        string? defaultNamespace = null;
        string body = xquery;
        while (Prolog().Match(body) is { Success: true } m)
        {
            if (m.Groups["default"].Success)
                defaultNamespace = m.Groups["uri"].Value;
            else
            {
                namespaces.RemoveAll(n => n.Prefix == m.Groups["prefix"].Value);
                namespaces.Add((m.Groups["prefix"].Value, m.Groups["uri"].Value));
            }
            body = body[m.Length..];
        }
        body = body.Trim();
        if (body.Length == 0)
            throw new XQueryNotXPathException("an empty path");

        string? defaultPrefix = null;
        if (defaultNamespace is not null)
        {
            defaultPrefix = "d";
            for (int i = 1; namespaces.Any(n => n.Prefix == defaultPrefix); i++)
                defaultPrefix = "d" + i;
            namespaces.Add((defaultPrefix, defaultNamespace));
        }
        return new XPathExpression(Rewrite(body, defaultPrefix), namespaces);
    }

    private enum Kind { Name, Literal, Number, Symbol }

    /// <param name="Lead">The whitespace before the token, kept in the output.</param>
    private sealed record Token(Kind Kind, string Text, string Lead = "");

    private static string Rewrite(string body, string? defaultPrefix)
    {
        var tokens = Tokenize(body);
        var output = new StringBuilder();
        // Parentheses opened by a function call returning a string, number or boolean.
        var parens = new Stack<bool>();
        bool dropNextSingleton = false;
        string? axis = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            var prev = i > 0 ? tokens[i - 1] : null;
            var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            if (t.Kind == Kind.Symbol && t.Text is "$" or "{" or "}")
                throw new XQueryNotXPathException(t.Text == "$" ? "a variable" : "an XQuery constructor");
            if (t.Kind == Kind.Name && (XQueryOnly.Contains(t.Text) || t.Text.StartsWith("xs:", StringComparison.Ordinal)))
                throw new XQueryNotXPathException($"the XQuery function {t.Text}");
            if (t.Kind == Kind.Name && t.Text is "for" or "let" or "return" or "if" or "then" or "else" or "some" or "every" or "instance" or "cast" or "treat"
                && !IsNameTestPosition(prev))
                throw new XQueryNotXPathException($"the XQuery expression '{t.Text}'");

            if (dropNextSingleton)
            {
                dropNextSingleton = false;
                if (t.Text == "[" && i + 2 < tokens.Count && tokens[i + 1].Text == "1" && tokens[i + 2].Text == "]")
                {
                    i += 2;
                    continue;
                }
            }
            switch (t)
            {
                case { Kind: Kind.Symbol, Text: "(" }:
                    parens.Push(prev is { Kind: Kind.Name } && !NodeFunctions.Contains(prev.Text) && IsNameTestPosition(i > 1 ? tokens[i - 2] : null));
                    break;
                case { Kind: Kind.Symbol, Text: ")" }:
                    dropNextSingleton = parens.Count > 0 && parens.Pop();
                    break;
            }
            output.Append(t.Lead);
            if (t.Kind == Kind.Name && defaultPrefix is not null && !t.Text.Contains(':') && IsNameTestPosition(prev)
                && next?.Text is not "(" and not "::" && prev?.Text != "@" && axis is not ("attribute" or "namespace"))
                output.Append(defaultPrefix).Append(':');
            output.Append(t.Text);
            if (t.Kind == Kind.Name && next?.Text == "::")
                axis = t.Text;
            else if (t.Kind == Kind.Name)
                axis = null;
        }
        return output.ToString();
    }

    /// <summary>
    /// A name after nothing, an operator or <c>( [ , / // | @ ::</c> is a name test or a function;
    /// after an operand (a name, <c>) ]</c>, a literal, <c>.</c>) it is an operator (<c>and</c>, <c>div</c>…).
    /// </summary>
    private static bool IsNameTestPosition(Token? prev) => prev switch
    {
        null => true,
        { Kind: Kind.Name } => Operators.Contains(prev.Text),
        { Kind: Kind.Literal or Kind.Number } => false,
        { Text: ")" or "]" or "." or ".." or "*" } => false,
        _ => true,
    };

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < s.Length)
        {
            int leadStart = i;
            while (i < s.Length && char.IsWhiteSpace(s[i]))
                i++;
            if (i == s.Length)
                break;
            string lead = s[leadStart..i];
            char ch = s[i];
            if (ch is '"' or '\'')
            {
                int end = s.IndexOf(ch, i + 1);
                if (end < 0)
                    throw new XQueryNotXPathException("an unterminated string");
                tokens.Add(new Token(Kind.Literal, s[i..(end + 1)], lead));
                i = end + 1;
                continue;
            }
            if (char.IsDigit(ch) || (ch == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                    i++;
                tokens.Add(new Token(Kind.Number, s[start..i], lead));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '.' or '-' or '_'
                       || (s[i] == ':' && i + 1 < s.Length && s[i + 1] != ':' && (char.IsLetter(s[i + 1]) || s[i + 1] is '_' or '*'))))
                    i++;
                if (i < s.Length && s[i - 1] == ':' && s[i] == '*')
                    i++;
                tokens.Add(new Token(Kind.Name, s[start..i], lead));
                continue;
            }
            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            if (two is "//" or "::" or ".." or "!=" or "<=" or ">=")
            {
                tokens.Add(new Token(Kind.Symbol, two, lead));
                i += 2;
                continue;
            }
            tokens.Add(new Token(Kind.Symbol, ch.ToString(), lead));
            i++;
        }
        return tokens;
    }

    [GeneratedRegex("""^\s*declare\s+(?:(?<default>default\s+element\s+namespace)\s*|namespace\s+(?<prefix>[A-Za-z_][\w.-]*)\s*=\s*)(?<q>["'])(?<uri>.*?)\k<q>\s*;""", RegexOptions.Singleline)]
    private static partial Regex Prolog();
}

/// <summary>An xml method's XQuery uses something XPath 1.0 does not have.</summary>
public sealed class XQueryNotXPathException(string what) : Exception($"{what} is XQuery that XPath 1.0 does not have")
{
    public string What { get; } = what;
}
