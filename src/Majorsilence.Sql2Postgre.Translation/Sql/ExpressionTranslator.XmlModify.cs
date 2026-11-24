using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Majorsilence.Sql2Postgre.Model;
using Majorsilence.Sql2Postgre.Translation.PgAst;
using Majorsilence.Sql2Postgre.Translation.Semantics;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// xml <c>modify()</c> (WP 4.24): <c>SET col.modify('…')</c> and <c>SET @x.modify('…')</c> become
/// an assignment of the mssql_compat <c>xml_modify</c> helpers' result, which rebuild the document's
/// text at the node the target path finds:
/// <list type="bullet">
/// <item><c>insert &lt;constructor&gt; (as first | as last)? into | before | after (path)[1]</c>:
/// one element constructor without enclosed expressions, written as SQL Server writes it
/// (boundary whitespace dropped, empty elements as <c>&lt;a/&gt;</c>).</item>
/// <item><c>replace value of (path)[1] with expr</c>: the target a text node, an attribute
/// without a prefix, or (typed xml) an element of simple type; <c>expr</c> strings, numbers,
/// <c>sql:column</c>, <c>sql:variable</c>, <c>data(path)</c>, paths, and <c>+ - * div</c> of them,
/// typed as XQuery types them and written as SQL Server writes the type.</item>
/// </list>
/// Anything else (<c>delete</c>, sequences, other functions) is reported (S2P3040).
/// </summary>
public sealed partial class ExpressionTranslator
{
    private const string XmlModifyHelper = "xml_modify";

    /// <summary>The new value of <paramref name="document"/> (an xml column or variable) after <c>modify(dml)</c>.</summary>
    /// <param name="schemaCollection">Typed xml's schema collection, or null for untyped xml.</param>
    public PgExpr XmlModify(TSqlFragment source, IList<ScalarExpression> parameters, ScalarExpression document, string? schemaCollection, ExpressionContext c)
    {
        if (parameters is not [StringLiteral dml])
            throw Untranslatable(source, "xml modify() whose argument is not a string literal", DiagnosticCodes.XmlModify);
        var types = schemaCollection is null ? null : model.XmlSchemaTypesOf(schemaCollection, c.Schema ?? "dbo");
        var result = new XmlDml(this, source, dml.Value, Scalar(document, c), schemaCollection is not null, types, c).Translate();
        c.Report(DiagnosticCodes.XmlModifyRebuilt, Severity.Info,
            "xml modify() rebuilds the document's text with mssql_compat.xml_modify_*. The document is the same, but an inserted element "
            + "declares its namespace with xmlns where SQL Server may write a prefix, and in typed xml the inserted values keep the text "
            + "they are written with where SQL Server stores them normalised (0.00 as 0).");
        return result;
    }

    private enum XmlTargetKind { Element, Text, Attribute }

    private sealed partial class XmlDml(ExpressionTranslator x, TSqlFragment source, string dml, PgExpr document, bool typed, XmlSchemaTypes? types, ExpressionContext c)
    {
        private string _prolog = "";
        private string? _defaultNamespace;
        private IReadOnlyList<(string Prefix, string Uri)> _namespaces = [];

        // The value expression being read, and the position in it.
        private string _text = "";
        private int _pos;

        public PgExpr Translate()
        {
            var (prolog, defaultNamespace, namespaces, body) = XQueryPath.SplitProlog(dml);
            _prolog = prolog;
            _defaultNamespace = defaultNamespace;
            _namespaces = namespaces;
            if (InsertStart().Match(body) is { Success: true } insert)
                return Insert(body[insert.Length..]);
            if (ReplaceStart().Match(body) is { Success: true } replace)
                return Replace(body[replace.Length..]);
            string verb = Regex.Match(body, @"^\S+").Value;
            throw Report($"xml modify() with '{verb}'");
        }

        private UntranslatableException Report(string what) =>
            Untranslatable(source, $"{what} (PostgreSQL's xml type has no update functions: build the new document instead)", DiagnosticCodes.XmlModify);

        // ---- insert ----

        private PgExpr Insert(string rest)
        {
            rest = rest.TrimStart();
            if (!rest.StartsWith('<'))
                throw Report("xml modify() inserting something other than one element constructor");
            int end = ConstructorEnd(rest);
            string node = Constructor(rest[..end]);
            var m = InsertPlace().Match(rest[end..].Trim());
            if (!m.Success)
                throw Report("xml modify() inserting something other than one element constructor");
            string place = m.Groups["place"].Success ? m.Groups["place"].Value : "last";
            var (path, kind, _) = Target(m.Groups["target"].Value);
            if (kind != XmlTargetKind.Element)
                throw Report("xml modify() inserting into, before or after a node that is not an element");
            return x.CompatIn(XmlModifyHelper, "xml_modify_insert",
                [document, PgLiteral.String(path), Namespaces(), PgLiteral.String(node), PgLiteral.String(_defaultNamespace ?? ""), PgLiteral.String(place)],
                c, source);
        }

        /// <summary>The length of the element constructor at the start of <paramref name="s"/>.</summary>
        private int ConstructorEnd(string s)
        {
            int depth = 0;
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] != '<')
                {
                    i++;
                    continue;
                }
                string close = s.AsSpan(i).StartsWith("<!--") ? "-->" : s.AsSpan(i).StartsWith("<![CDATA[") ? "]]>" : s.AsSpan(i).StartsWith("<?") ? "?>" : "";
                if (close.Length > 0)
                {
                    int j = s.IndexOf(close, i, StringComparison.Ordinal);
                    if (j < 0)
                        break;
                    i = j + close.Length;
                    continue;
                }
                int tagEnd = i + 1;
                char? quote = null;
                while (tagEnd < s.Length && (quote is not null || s[tagEnd] != '>'))
                {
                    if (quote is null && s[tagEnd] is '"' or '\'')
                        quote = s[tagEnd];
                    else if (quote == s[tagEnd])
                        quote = null;
                    tagEnd++;
                }
                if (tagEnd >= s.Length)
                    break;
                if (s[i + 1] == '/')
                    depth--;
                else if (s[tagEnd - 1] != '/')
                    depth++;
                i = tagEnd + 1;
                if (depth == 0)
                    return i;
            }
            throw Report("an xml modify() element constructor that does not end");
        }

        /// <summary>
        /// The constructor as SQL Server writes the node it makes: boundary whitespace (text of only
        /// whitespace between tags) dropped, CDATA as escaped text, attributes in double quotes,
        /// elements without content as <c>&lt;a/&gt;</c>.
        /// </summary>
        private string Constructor(string xml)
        {
            if (xml.Contains('{') || xml.Contains('}'))
                throw Report("an xml modify() element constructor with enclosed expressions");
            var sb = new StringBuilder();
            var names = new Stack<string>();
            var text = new StringBuilder();
            bool significant = false;
            bool open = false;
            int roots = 0;

            void Content()
            {
                if (open)
                {
                    sb.Append('>');
                    open = false;
                }
            }

            void Flush()
            {
                if (text.Length > 0 && significant)
                {
                    Content();
                    sb.Append(EscapeText(text.ToString()));
                }
                text.Clear();
                significant = false;
            }

            try
            {
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                });
                while (reader.Read())
                {
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Text or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                            text.Append(reader.Value);
                            significant |= reader.NodeType == XmlNodeType.Text;
                            break;
                        case XmlNodeType.CDATA:
                            text.Append(reader.Value);
                            significant = true;
                            break;
                        case XmlNodeType.Element:
                            Flush();
                            if (names.Count == 0 && ++roots > 1)
                                throw Report("xml modify() inserting more than one node");
                            if (reader.Prefix.Length > 0 || reader.NamespaceURI.Length > 0)
                                throw Report("an xml modify() element constructor with namespace prefixes or declarations");
                            Content();
                            sb.Append('<').Append(reader.LocalName);
                            bool empty = reader.IsEmptyElement;
                            if (reader.MoveToFirstAttribute())
                            {
                                do
                                {
                                    if (reader.Prefix.Length > 0 || reader.LocalName == "xmlns")
                                        throw Report("an xml modify() element constructor with namespace prefixes or declarations");
                                    sb.Append(' ').Append(reader.LocalName).Append("=\"").Append(EscapeAttribute(reader.Value)).Append('"');
                                }
                                while (reader.MoveToNextAttribute());
                                reader.MoveToElement();
                            }
                            if (empty)
                                sb.Append("/>");
                            else
                            {
                                names.Push(reader.LocalName);
                                open = true;
                            }
                            break;
                        case XmlNodeType.EndElement:
                            Flush();
                            names.Pop();
                            if (open)
                            {
                                sb.Append("/>");
                                open = false;
                            }
                            else
                                sb.Append("</").Append(reader.LocalName).Append('>');
                            break;
                        case XmlNodeType.Comment:
                            Flush();
                            Content();
                            sb.Append("<!--").Append(reader.Value).Append("-->");
                            break;
                        case XmlNodeType.ProcessingInstruction:
                            Flush();
                            Content();
                            sb.Append("<?").Append(reader.Name).Append(reader.Value.Length > 0 ? " " + reader.Value : "").Append("?>");
                            break;
                        default:
                            throw Report($"an xml modify() element constructor with {reader.NodeType}");
                    }
                    if (names.Count == 0 && significant)
                        throw Report("xml modify() inserting more than one node");
                }
            }
            catch (XmlException)
            {
                throw Report("an xml modify() element constructor that is not well-formed XML");
            }
            if (roots != 1)
                throw Report("xml modify() inserting something other than one element constructor");
            return sb.ToString();
        }

        // ---- replace value of ----

        private PgExpr Replace(string rest)
        {
            int with = TopLevelKeyword(rest, "with");
            if (with < 0)
                throw Report("xml modify() 'replace value of' without 'with'");
            var (path, kind, attribute) = Target(rest[..with]);
            _text = rest[(with + "with".Length)..];
            _pos = 0;
            var (value, type) = Additive();
            Skip();
            if (_pos < _text.Length)
                throw Report($"the XQuery expression '{_text[_pos..].Trim()}' in xml modify()");
            return x.CompatIn(XmlModifyHelper, "xml_modify_replace",
                [document, PgLiteral.String(path), Namespaces(), PgLiteral.String(kind.ToString().ToLowerInvariant()),
                    attribute is null ? new PgLiteral("NULL") : PgLiteral.String(attribute), Text(value, type)],
                c, source);
        }

        /// <summary>Where <paramref name="keyword"/> is, outside parentheses, brackets and string literals; -1 when it is not.</summary>
        private static int TopLevelKeyword(string s, string keyword)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch is '"' or '\'')
                {
                    int j = s.IndexOf(ch, i + 1);
                    i = j < 0 ? s.Length : j;
                }
                else if (ch is '(' or '[')
                    depth++;
                else if (ch is ')' or ']')
                    depth--;
                else if (depth == 0 && i > 0 && char.IsWhiteSpace(s[i - 1]) && string.CompareOrdinal(s, i, keyword, 0, keyword.Length) == 0
                    && (i + keyword.Length == s.Length || char.IsWhiteSpace(s[i + keyword.Length]) || s[i + keyword.Length] == '('))
                    return i;
            }
            return -1;
        }

        // ---- the target path ----

        /// <summary>The XPath of a target, what kind of node its last step finds, and the attribute's name.</summary>
        private (string Path, XmlTargetKind Kind, string? Attribute) Target(string xquery)
        {
            string path = XPathOf(xquery);
            string last = LastStep(xquery);
            if (last == "text()")
                return (path, XmlTargetKind.Text, null);
            if (last.StartsWith('@'))
            {
                string name = last[1..].Trim();
                if (!NcName().IsMatch(name))
                    throw Report($"xml modify() on the attribute {last} (only an attribute named without a prefix is translated)");
                return (path, XmlTargetKind.Attribute, name);
            }
            return (path, XmlTargetKind.Element, null);
        }

        /// <summary>An absolute XQuery path in XPath 1.0, with the prolog's namespaces.</summary>
        private string XPathOf(string xquery)
        {
            string trimmed = xquery.Trim();
            if (!trimmed.TrimStart('(', ' ', '\t', '\r', '\n').StartsWith('/'))
                throw Report($"xml modify() with the relative path '{trimmed}'");
            try
            {
                return XQueryPath.Convert(_prolog + trimmed, []).Path;
            }
            catch (XQueryNotXPathException ex)
            {
                throw Report($"xml modify() with a path that uses {ex.What}");
            }
        }

        /// <summary>The prolog's namespaces as the path translation binds them (the default element namespace to a prefix of its own).</summary>
        private List<(string Prefix, string Uri)> AllNamespaces() => XQueryPath.Convert(_prolog + "/x", []).Namespaces.ToList();

        /// <summary>The last step of a path, without its predicates and the parentheses around the path.</summary>
        private static string LastStep(string xquery)
        {
            string s = xquery.Trim();
            while (true)
            {
                s = s.Trim();
                if (s.EndsWith(']') && Matching(s, s.Length - 1, '[', ']') is int open and > 0)
                    s = s[..open];
                else if (s.StartsWith('(') && Matching(s, s.Length - 1, '(', ')') == 0)
                    s = s[1..^1];
                else
                    break;
            }
            int depth = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (s[i] is ']' or ')')
                    depth++;
                else if (s[i] is '[' or '(')
                    depth--;
                else if (s[i] == '/' && depth == 0)
                    return Unpredicated(s[(i + 1)..]);
            }
            return Unpredicated(s);
        }

        private static string Unpredicated(string step)
        {
            step = step.Trim();
            while (step.EndsWith(']') && Matching(step, step.Length - 1, '[', ']') is int open and > 0)
                step = step[..open].Trim();
            return step;
        }

        /// <summary>The position of the bracket that opens the one closing at <paramref name="close"/>.</summary>
        private static int? Matching(string s, int close, char opening, char closing)
        {
            int depth = 0;
            for (int i = close; i >= 0; i--)
            {
                if (s[i] == closing)
                    depth++;
                else if (s[i] == opening && --depth == 0)
                    return i;
            }
            return null;
        }

        private PgExpr Namespaces()
        {
            var all = AllNamespaces();
            string literal = all.Count == 0 ? "{}"
                : "{" + string.Join(",", all.Select(n => "{" + ArrayElement(n.Prefix) + "," + ArrayElement(n.Uri) + "}")) + "}";
            return new PgCast(PgLiteral.String(literal), "text[]");
        }

        private static string ArrayElement(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        // ---- the new value ----

        private (PgExpr Value, XQueryType? Type) Additive()
        {
            var left = Multiplicative();
            while (true)
            {
                Skip();
                if (_pos < _text.Length && _text[_pos] is '+' or '-')
                {
                    string op = _text[_pos++].ToString();
                    left = Arithmetic(op, left, Multiplicative());
                }
                else
                    return left;
            }
        }

        private (PgExpr Value, XQueryType? Type) Multiplicative()
        {
            var left = Unary();
            while (true)
            {
                Skip();
                if (_pos < _text.Length && _text[_pos] == '*')
                {
                    _pos++;
                    left = Arithmetic("*", left, Unary());
                }
                else if (Keyword("div"))
                    left = Arithmetic("div", left, Unary());
                else if (Keyword("idiv") || Keyword("mod"))
                    throw Report("xml modify() with idiv or mod");
                else
                    return left;
            }
        }

        private (PgExpr Value, XQueryType? Type) Unary()
        {
            Skip();
            if (_pos < _text.Length && _text[_pos] is '-' or '+')
            {
                bool minus = _text[_pos++] == '-';
                var (value, type) = Unary();
                if (!minus)
                    return (value, type);
                Numeric("-", type);
                return type is XQueryType.Untyped or XQueryType.Double
                    ? (new PgUnary("-", As(value, "float8")), XQueryType.Double)
                    : (new PgUnary("-", As(value, "numeric")), type);
            }
            return Primary();
        }

        private (PgExpr Value, XQueryType? Type) Primary()
        {
            Skip();
            if (_pos >= _text.Length)
                throw Report("xml modify() 'replace value of' without a value");
            char ch = _text[_pos];
            (PgExpr Value, XQueryType? Type) result;
            if (ch is '"' or '\'')
                result = (PgLiteral.String(StringLiteral()), XQueryType.String);
            else if (char.IsAsciiDigit(ch) || (ch == '.' && _pos + 1 < _text.Length && char.IsAsciiDigit(_text[_pos + 1])))
                result = NumberLiteral();
            else if (ch == '/' || (ch == '(' && _text[(_pos + 1)..].TrimStart().StartsWith('/')))
                result = NodeValue(PathText());
            else if (ch == '(')
            {
                _pos++;
                Skip();
                if (_pos < _text.Length && _text[_pos] == ')')
                    throw Report("xml modify() replacing a value with the empty sequence ()");
                result = Additive();
                Expect(')');
            }
            else if (Function("data") is { } dataPath)
                result = NodeValue(dataPath);
            else if (Function("string") is { } stringPath)
                result = (NodeValue(stringPath).Value, XQueryType.String);
            else if (Function("sql:column") is { } column)
                result = Sql(Name(column));
            else if (Function("sql:variable") is { } variable)
                result = Sql(Name(variable));
            else
                throw Report($"the XQuery expression '{_text[_pos..].Trim()}' in xml modify()");
            Singleton();
            return result;
        }

        /// <summary><c>name(…)</c> at the position: the text between its parentheses, or null when the name is not there.</summary>
        private string? Function(string name)
        {
            int at = _pos;
            if (string.CompareOrdinal(_text, at, name, 0, name.Length) != 0)
                return null;
            int i = at + name.Length;
            while (i < _text.Length && char.IsWhiteSpace(_text[i]))
                i++;
            if (i >= _text.Length || _text[i] != '(')
                return null;
            int depth = 0;
            for (int j = i; j < _text.Length; j++)
            {
                if (_text[j] is '"' or '\'')
                {
                    int k = _text.IndexOf(_text[j], j + 1);
                    j = k < 0 ? _text.Length : k;
                }
                else if (_text[j] == '(')
                    depth++;
                else if (_text[j] == ')' && --depth == 0)
                {
                    _pos = j + 1;
                    return _text[(i + 1)..j];
                }
            }
            throw Report($"the XQuery expression '{_text[at..].Trim()}' in xml modify()");
        }

        /// <summary>A path written as a value: up to the first whitespace or operator outside brackets.</summary>
        private string PathText()
        {
            int start = _pos;
            int depth = 0;
            while (_pos < _text.Length)
            {
                char ch = _text[_pos];
                if (ch is '"' or '\'')
                {
                    int k = _text.IndexOf(ch, _pos + 1);
                    _pos = k < 0 ? _text.Length : k + 1;
                    continue;
                }
                if (ch is '(' or '[')
                    depth++;
                else if (ch is ')' or ']')
                {
                    if (depth == 0)
                        break;
                    depth--;
                }
                else if (depth == 0 && (char.IsWhiteSpace(ch) || ch is '+' or ','))
                    break;
                _pos++;
            }
            return _text[start.._pos];
        }

        /// <summary>A node's typed value (<c>data(path)</c>, or a path an operator atomizes): its text, with its type.</summary>
        private (PgExpr Value, XQueryType? Type) NodeValue(string path)
        {
            string xpath = XPathOf(path);
            PgExpr value = new PgSubquery(new PgSelect
            {
                Targets = [new PgTarget(new PgColumnRef("v"))],
                From = [new PgXmlTable(AllNamespaces(), "/", document, [new PgXmlTableColumn("v", "text", $"({xpath})[1]")])],
            });
            return (value, typed ? ElementType(LastStep(path)) : XQueryType.Untyped);
        }

        /// <summary>The type the schema collection gives an element step; null when it is not known.</summary>
        private XQueryType? ElementType(string step)
        {
            if (types is null || step.StartsWith('@') || step.Contains('(') || step.Contains('*'))
                return null;
            int colon = step.IndexOf(':');
            string ns = colon < 0 ? _defaultNamespace ?? "" : _namespaces.LastOrDefault(n => n.Prefix == step[..colon]).Uri ?? "";
            return types.Element(ns, colon < 0 ? step : step[(colon + 1)..]);
        }

        /// <summary>The name in <c>sql:column("t.c")</c> or <c>sql:variable("@v")</c>.</summary>
        private static string Name(string argument)
        {
            string name = argument.Trim();
            return name.Length >= 2 && name[0] is '"' or '\'' && name[^1] == name[0] ? name[1..^1] : name;
        }

        /// <summary>sql:column("t.c") or sql:variable("@v"): the value, typed as SQL Server types it in XQuery.</summary>
        private (PgExpr Value, XQueryType? Type) Sql(string name)
        {
            ScalarExpression e;
            if (name.StartsWith('@'))
                e = new VariableReference { Name = name };
            else
            {
                var identifier = new MultiPartIdentifier();
                foreach (string part in name.Split('.'))
                    identifier.Identifiers.Add(new Identifier { Value = part.Trim('[', ']') });
                e = new ColumnReferenceExpression { MultiPartIdentifier = identifier };
            }
            var sqlType = x.TypeOf(e, c);
            XQueryType type = sqlType?.Name switch
            {
                "tinyint" or "smallint" or "int" or "bigint" => XQueryType.Integer,
                "decimal" or "numeric" or "money" or "smallmoney" => XQueryType.Decimal,
                "float" => XQueryType.Double,
                "bit" => XQueryType.Boolean,
                "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" => XQueryType.String,
                _ => throw Report($"xml modify() reading {name} of type {sqlType?.ToString() ?? "unknown"} (only numbers, bit and strings are translated)"),
            };
            return (x.Scalar(e, c), type);
        }

        private string StringLiteral()
        {
            char quote = _text[_pos++];
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length)
                    throw Report("an XQuery string literal that does not end");
                char ch = _text[_pos++];
                if (ch == quote)
                {
                    if (_pos < _text.Length && _text[_pos] == quote)
                    {
                        sb.Append(quote);
                        _pos++;
                        continue;
                    }
                    break;
                }
                sb.Append(ch);
            }
            return Entities(sb.ToString());
        }

        /// <summary>XQuery string literals' entity and character references.</summary>
        private string Entities(string s) => EntityReference().Replace(s, m => m.Value switch
        {
            "&lt;" => "<",
            "&gt;" => ">",
            "&amp;" => "&",
            "&quot;" => "\"",
            "&apos;" => "'",
            _ when m.Groups["hex"].Success => char.ConvertFromUtf32(int.Parse(m.Groups["hex"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)),
            _ when m.Groups["dec"].Success => char.ConvertFromUtf32(int.Parse(m.Groups["dec"].Value, CultureInfo.InvariantCulture)),
            _ => throw Report($"the XQuery entity reference {m.Value}"),
        });

        private (PgExpr Value, XQueryType? Type) NumberLiteral()
        {
            var m = NumberToken().Match(_text, _pos);
            _pos += m.Length;
            string n = m.Value;
            if (n.Contains('e') || n.Contains('E'))
                return (new PgCast(PgLiteral.String(n), "float8"), XQueryType.Double);
            return (new PgCast(new PgLiteral(n.StartsWith('.') ? "0" + n : n), "numeric"), n.Contains('.') ? XQueryType.Decimal : XQueryType.Integer);
        }

        /// <summary>The <c>[1]</c> XQuery puts after a value to say it is one item.</summary>
        private void Singleton()
        {
            Skip();
            if (_pos >= _text.Length || _text[_pos] != '[')
                return;
            var m = SingletonPredicate().Match(_text, _pos);
            if (!m.Success)
                throw Report($"the XQuery predicate '{_text[_pos..].Trim()}' in xml modify()");
            _pos += m.Length;
        }

        private (PgExpr Value, XQueryType? Type) Arithmetic(string op, (PgExpr Value, XQueryType? Type) left, (PgExpr Value, XQueryType? Type) right)
        {
            Numeric(op, left.Type);
            Numeric(op, right.Type);
            string pgOp = op == "div" ? "/" : op;
            if (left.Type is XQueryType.Untyped or XQueryType.Double || right.Type is XQueryType.Untyped or XQueryType.Double)
                return (new PgBinary(pgOp, As(left.Value, "float8"), As(right.Value, "float8")), XQueryType.Double);
            if (op == "div")
                throw Report("xml modify() dividing decimals or integers (SQL Server's scale for the quotient is not translated)");
            return (new PgBinary(pgOp, As(left.Value, "numeric"), As(right.Value, "numeric")),
                left.Type == XQueryType.Decimal || right.Type == XQueryType.Decimal ? XQueryType.Decimal : XQueryType.Integer);
        }

        /// <summary>The value as numeric or float8, cast only when it is not one already.</summary>
        private static PgExpr As(PgExpr e, string type) => IsOf(e, type) ? e : new PgCast(e, type);

        private static bool IsOf(PgExpr e, string type) => e switch
        {
            PgCast cast => cast.Type == type,
            PgUnary { Operator: "-" } u => IsOf(u.Operand, type),
            PgBinary { Operator: "+" or "-" or "*" or "/" } b => IsOf(b.Left, type) && IsOf(b.Right, type),
            _ => false,
        };

        private void Numeric(string op, XQueryType? type)
        {
            if (type is null)
                throw Report($"xml modify() using '{op}' on a value whose type in the XML schema collection is not known");
            if (type is not (XQueryType.Untyped or XQueryType.Double or XQueryType.Decimal or XQueryType.Integer))
                throw Report($"xml modify() using '{op}' on a value of type {type}");
        }

        /// <summary>A value as SQL Server writes its type in a document.</summary>
        private PgExpr Text(PgExpr value, XQueryType? type) => type switch
        {
            XQueryType.Integer or XQueryType.Decimal => new PgCast(new PgFunctionCall("trim_scale", [As(value, "numeric")]), "text"),
            XQueryType.Double => x.CompatIn(XmlModifyHelper, "xquery_double", [As(value, "float8")], c, source),
            XQueryType.Boolean => new PgCase(null,
                [new PgWhen(value, PgLiteral.String("true")), new PgWhen(new PgUnary("NOT", value), PgLiteral.String("false"))], null),
            _ => new PgCast(value, "text"),
        };

        private bool Keyword(string keyword)
        {
            Skip();
            if (string.CompareOrdinal(_text, _pos, keyword, 0, keyword.Length) != 0)
                return false;
            int after = _pos + keyword.Length;
            if (after < _text.Length && (char.IsLetterOrDigit(_text[after]) || _text[after] is '-' or '_' or '.'))
                return false;
            _pos = after;
            return true;
        }

        private void Expect(char ch)
        {
            Skip();
            if (_pos >= _text.Length || _text[_pos] != ch)
                throw Report($"the XQuery expression '{_text.Trim()}' in xml modify()");
            _pos++;
        }

        private void Skip()
        {
            while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
                _pos++;
        }

        [GeneratedRegex(@"^insert\s")]
        private static partial Regex InsertStart();

        [GeneratedRegex(@"^replace\s+value\s+of\s")]
        private static partial Regex ReplaceStart();

        [GeneratedRegex(@"^(?:(?:as\s+(?<place>first|last)\s+)?into|(?<place>before|after))\s+(?<target>.+)$", RegexOptions.Singleline)]
        private static partial Regex InsertPlace();

        [GeneratedRegex(@"^[A-Za-z_][\w.\-]*$")]
        private static partial Regex NcName();

        [GeneratedRegex(@"&(?:#x(?<hex>[0-9A-Fa-f]+)|#(?<dec>[0-9]+)|[A-Za-z]+);")]
        private static partial Regex EntityReference();

        [GeneratedRegex(@"\G(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?")]
        private static partial Regex NumberToken();

        [GeneratedRegex(@"\G\[\s*1\s*\]")]
        private static partial Regex SingletonPredicate();
    }

    /// <summary>Text content as SQL Server writes it in a document (the helper's xml_modify_escape).</summary>
    private static string EscapeText(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#x0D;");

    /// <summary>An attribute value as SQL Server writes it.</summary>
    private static string EscapeAttribute(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;")
            .Replace("\t", "&#x09;").Replace("\n", "&#x0A;").Replace("\r", "&#x0D;");
}
