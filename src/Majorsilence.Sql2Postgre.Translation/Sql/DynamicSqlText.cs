using System.Globalization;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// The text of dynamic SQL (<c>EXEC (string)</c>, <c>sp_executesql</c>) as far as it is known
/// before it runs. A string held in a variable is followed back to the SET or DECLARE that last
/// gave it its value; pieces that are constants there (literals, variables last set to constants,
/// <c>NCHAR(13)</c>, <c>QUOTENAME(N'x')</c>, a CASE on constants) are folded into the text, as
/// SQL Server would put it together.
/// </summary>
/// <remarks>
/// A position is the statement lists around a statement, outermost first, each with the index of
/// the statement it is at. The value of a variable is known only when nothing between its last
/// plain assignment and that position can change it: an assignment anywhere in a WHILE around the
/// position (an earlier pass may have run it) makes it unknown.
/// </remarks>
public static class DynamicSqlText
{
    /// <summary>A variable's last assignment: its value (null for <c>DECLARE @v type</c>, which is NULL) and the position it was made at.</summary>
    public sealed record Assignment(ScalarExpression? Value, IReadOnlyList<(IList<TSqlStatement> List, int Index)> At, DataTypeReference? DeclaredType);

    /// <summary>Whether an EXEC runs dynamic SQL (a string, or sp_executesql).</summary>
    public static bool IsDynamic(ExecuteSpecification spec) =>
        spec.ExecutableEntity is ExecutableStringList
        || spec.ExecutableEntity is ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: var name }
        && name.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase);

    /// <summary>The expression giving the SQL text and the position it is read at: through a variable, its last plain assignment.</summary>
    public static (ScalarExpression Text, IReadOnlyList<(IList<TSqlStatement> List, int Index)> At)? Text(
        ExecuteStatement e, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames)
    {
        ScalarExpression? text = e.ExecuteSpecification.ExecutableEntity switch
        {
            ExecutableStringList strings => Concatenation(strings.Strings),
            ExecutableProcedureReference p when p.Parameters.Count > 0 && p.Parameters[0].Variable is null or { Name: "@stmt" or "@statement" } =>
                p.Parameters[0].ParameterValue,
            _ => null,
        };
        if (text is VariableReference v)
            return LastAssignment(v.Name, frames) is { Value: { } value } a ? (value, a.At) : null;
        return text is null ? null : (text, frames);
    }

    private static ScalarExpression? Concatenation(IList<ValueExpression> pieces)
    {
        if (pieces.Count == 0)
            return null;
        ScalarExpression result = pieces[0];
        for (int i = 1; i < pieces.Count; i++)
            result = new BinaryExpression { BinaryExpressionType = BinaryExpressionType.Add, FirstExpression = result, SecondExpression = pieces[i] };
        return result;
    }

    /// <summary>
    /// The last <c>SET @v = …</c> or <c>DECLARE @v … [= …]</c> before this position, when nothing
    /// else can assign @v in between; null otherwise.
    /// </summary>
    public static Assignment? LastAssignment(string variable, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames)
    {
        for (int f = frames.Count - 1; f >= 0; f--)
        {
            var (list, index) = frames[f];
            // Inside a loop, a later statement of the loop may have assigned @v on an earlier pass.
            if (f > 0 && frames[f - 1].List[frames[f - 1].Index] is WhileStatement loop && Assigns(loop, variable))
                return null;
            for (int i = index - 1; i >= 0; i--)
            {
                var s = list[i];
                var at = frames.Take(f).Append((list, i)).ToList();
                if (s is SetVariableStatement { AssignmentKind: AssignmentKind.Equals, CursorDefinition: null } set
                    && set.Variable.Name.Equals(variable, StringComparison.OrdinalIgnoreCase))
                    return new Assignment(set.Expression, at, DeclaredType(variable, at));
                if (s is DeclareVariableStatement d
                    && d.Declarations.FirstOrDefault(x => x.VariableName.Value.Equals(variable, StringComparison.OrdinalIgnoreCase)) is { } declared)
                    return new Assignment(declared.Value, at, declared.DataType);
                if (Assigns(s, variable))
                    return null;
            }
        }
        return null;
    }

    /// <summary>The type a variable was declared with, looking back from this position (null for a parameter, or not found).</summary>
    private static DataTypeReference? DeclaredType(string variable, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames)
    {
        for (int f = frames.Count - 1; f >= 0; f--)
        {
            var (list, index) = frames[f];
            for (int i = index; i >= 0; i--)
                if (list[i] is DeclareVariableStatement d
                    && d.Declarations.FirstOrDefault(x => x.VariableName.Value.Equals(variable, StringComparison.OrdinalIgnoreCase)) is { } declared)
                    return declared.DataType;
        }
        return null;
    }

    /// <summary>Whether a statement (or anything in it) can assign the variable.</summary>
    public static bool Assigns(TSqlFragment s, string variable)
    {
        bool Is(VariableReference? v) => v is not null && v.Name.Equals(variable, StringComparison.OrdinalIgnoreCase);
        var finder = new Finder(f => f switch
        {
            SetVariableStatement set => Is(set.Variable),
            SelectSetVariable select => Is(select.Variable),
            DeclareVariableElement d => d.VariableName.Value.Equals(variable, StringComparison.OrdinalIgnoreCase),
            FetchCursorStatement fetch => fetch.IntoVariables.Any(Is),
            ExecuteParameter { IsOutput: true, ParameterValue: VariableReference v } => Is(v),
            ExecuteSpecification { Variable: var status } => Is(status),
            _ => false,
        });
        s.Accept(finder);
        return finder.Found;
    }

    private sealed class Finder(Func<TSqlFragment, bool> test) : TSqlFragmentVisitor
    {
        public bool Found { get; private set; }

        public override void Visit(TSqlFragment fragment)
        {
            if (!Found && test(fragment))
                Found = true;
        }
    }

    /// <summary>The pieces of a string concatenation (<c>+</c> or CONCAT), in order.</summary>
    public static void Pieces(ScalarExpression e, List<ScalarExpression> pieces)
    {
        switch (e)
        {
            case ParenthesisExpression p:
                Pieces(p.Expression, pieces);
                break;
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } b:
                Pieces(b.FirstExpression, pieces);
                Pieces(b.SecondExpression, pieces);
                break;
            case FunctionCall { CallTarget: null, FunctionName.Value: var name } f when name.Equals("CONCAT", StringComparison.OrdinalIgnoreCase):
                foreach (var a in f.Parameters)
                    Pieces(a, pieces);
                break;
            default:
                pieces.Add(e);
                break;
        }
    }

    /// <summary>
    /// Every dynamic EXEC in a body, with its position: each IF branch and loop body is a list of
    /// its own (a BEGIN … END its statements), under the statement that holds it.
    /// </summary>
    public static IEnumerable<(ExecuteStatement Execute, IReadOnlyList<(IList<TSqlStatement> List, int Index)> At)> DynamicExecutes(IList<TSqlStatement> body)
    {
        var frames = new List<(IList<TSqlStatement> List, int Index)>();
        var found = new List<(ExecuteStatement, IReadOnlyList<(IList<TSqlStatement> List, int Index)>)>();

        void Walk(IList<TSqlStatement> list)
        {
            frames.Add((list, 0));
            for (int i = 0; i < list.Count; i++)
            {
                frames[^1] = (list, i);
                switch (list[i])
                {
                    case ExecuteStatement e when IsDynamic(e.ExecuteSpecification):
                        found.Add((e, frames.ToList()));
                        break;
                    case BeginEndBlockStatement b:
                        Walk(b.StatementList.Statements);
                        break;
                    case IfStatement s:
                        Nested(s.ThenStatement);
                        if (s.ElseStatement is not null)
                            Nested(s.ElseStatement);
                        break;
                    case WhileStatement w:
                        Nested(w.Statement);
                        break;
                    case TryCatchStatement tc:
                        Walk(tc.TryStatements.Statements);
                        Walk(tc.CatchStatements.Statements);
                        break;
                }
            }
            frames.RemoveAt(frames.Count - 1);
        }

        void Nested(TSqlStatement s) => Walk(s is BeginEndBlockStatement b ? b.StatementList.Statements : [s]);

        Walk(body);
        return found;
    }

    /// <summary>
    /// The procedures, functions, triggers and security policies a body creates with dynamic SQL
    /// whose whole text is known, each with its CREATE statement and that statement's text.
    /// </summary>
    public static IEnumerable<(TSqlStatement Statement, string Text)> CreatedModules(IList<TSqlStatement> body)
    {
        foreach (var (e, at) in DynamicExecutes(body))
        {
            if (Constant(e, at) is not { } sql)
                continue;
            var fragment = new TSql160Parser(initialQuotedIdentifiers: true).Parse(new StringReader(sql), out var errors);
            if (errors.Count > 0 || fragment is not TSqlScript script)
                continue;
            foreach (var s in script.Batches.SelectMany(b => b.Statements))
                if (s is ProcedureStatementBody or FunctionStatementBody or TriggerStatementBody or CreateSecurityPolicyStatement)
                    yield return (s, sql.Substring(s.StartOffset, s.FragmentLength));
        }
    }

    /// <summary>The name a CREATE PROCEDURE / FUNCTION / TRIGGER gives, as written (null for another statement).</summary>
    public static SchemaObjectName? CreatedName(TSqlStatement s) => s switch
    {
        ProcedureStatementBody p => p.ProcedureReference.Name,
        FunctionStatementBody f => f.Name,
        TriggerStatementBody t => t.Name,
        _ => null,
    };

    /// <summary>Two definitions, the same but for whitespace.</summary>
    public static bool SameDefinition(string a, string b) =>
        string.Equals(System.Text.RegularExpressions.Regex.Replace(a.Trim(), @"\s+", " "),
            System.Text.RegularExpressions.Regex.Replace(b.Trim(), @"\s+", " "), StringComparison.Ordinal);

    /// <summary>The whole text, when every piece of it is a constant here; null otherwise (or when it is NULL).</summary>
    public static string? Constant(ExecuteStatement e, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames) =>
        Text(e, frames) is var (text, at) && TryFold(text, at, out string? value) ? value : null;

    /// <summary>
    /// The value of a string expression known at this position: true with the text, or with null
    /// for NULL; false when it is not known. Only string values fold: a number becomes text only
    /// through CAST/CONVERT/STR, as T-SQL would otherwise add instead of concatenate.
    /// </summary>
    public static bool TryFold(ScalarExpression e, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames, out string? value) =>
        TryFold(e, frames, out value, 0);

    private static bool TryFold(ScalarExpression e, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames, out string? value, int depth)
    {
        value = null;
        if (depth > 32)
            return false;
        switch (e)
        {
            case StringLiteral s:
                value = s.Value;
                return true;
            case NullLiteral:
                return true;
            case ParenthesisExpression p:
                return TryFold(p.Expression, frames, out value, depth + 1);
            case BinaryExpression { BinaryExpressionType: BinaryExpressionType.Add } b:
            {
                // A long concatenation is a deep tree: fold its pieces one by one.
                var pieces = new List<ScalarExpression>();
                Pieces(b, pieces);
                var text = new System.Text.StringBuilder();
                bool isNull = false;
                foreach (var piece in pieces)
                {
                    if (!TryFold(piece, frames, out var part, depth + 1))
                        return false;
                    if (part is null)
                        isNull = true;
                    else
                        text.Append(part);
                }
                value = isNull ? null : text.ToString();
                return true;
            }
            case VariableReference v:
                if (LastAssignment(v.Name, frames) is not { } a || !IsVariableString(a.DeclaredType, out int? length))
                    return false;
                if (a.Value is null)
                    return true;
                if (!TryFold(a.Value, a.At, out value, depth + 1))
                    return false;
                if (value is not null && length is { } n && value.Length > n)
                    value = value[..n];
                return true;
            case FunctionCall { CallTarget: null, FunctionName.Value: var fn } f:
                return FoldFunction(fn.ToUpperInvariant(), f.Parameters, frames, out value, depth);
            case CastCall cast:
                return FoldCast(cast.Parameter, cast.DataType, frames, out value, depth);
            case ConvertCall { Style: null } convert:
                return FoldCast(convert.Parameter, convert.DataType, frames, out value, depth);
            case CoalesceExpression c:
                foreach (var x in c.Expressions)
                {
                    if (!TryFold(x, frames, out value, depth + 1))
                        return false;
                    if (value is not null)
                        return true;
                }
                return true;
            case SearchedCaseExpression c:
                foreach (var when in c.WhenClauses)
                {
                    if (TryTest(when.WhenExpression, frames, out bool? test, depth) is false)
                        return false;
                    if (test == true)
                        return TryFold(when.ThenExpression, frames, out value, depth + 1);
                }
                return c.ElseExpression is null || TryFold(c.ElseExpression, frames, out value, depth + 1);
            default:
                return false;
        }
    }

    private static bool FoldFunction(string fn, IList<ScalarExpression> args, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames, out string? value, int depth)
    {
        value = null;
        switch (fn)
        {
            case "NCHAR" or "CHAR" when args is [IntegerLiteral { Value: var code }]
                                       && int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n is > 0 and < 128:
                value = ((char)n).ToString();
                return true;
            case "QUOTENAME" when args.Count is 1 or 2:
                if (!TryFold(args[0], frames, out var name, depth + 1))
                    return false;
                string quote = "]";
                if (args.Count == 2)
                {
                    if (!TryFold(args[1], frames, out var q, depth + 1) || q is not { Length: 1 })
                        return false;
                    quote = q;
                }
                if (name is null || name.Length > 128)
                    return true; // NULL
                (string open, string close) = quote switch
                {
                    "[" or "]" => ("[", "]"),
                    "\"" => ("\"", "\""),
                    "'" => ("'", "'"),
                    "`" => ("`", "`"),
                    _ => ("", ""),
                };
                if (open.Length == 0)
                    return true; // NULL for any other quote character
                value = open + name.Replace(close, close + close, StringComparison.Ordinal) + close;
                return true;
            case "ISNULL" when args.Count == 2:
                if (!TryFold(args[0], frames, out value, depth + 1))
                    return false;
                return value is not null || TryFold(args[1], frames, out value, depth + 1);
            default:
                return false;
        }
    }

    private static bool FoldCast(ScalarExpression operand, DataTypeReference type, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames, out string? value, int depth)
    {
        value = null;
        if (!IsVariableString(type, out int? length, unsizedLength: 30))
            return false;
        if (operand is IntegerLiteral i)
            value = i.Value;
        else if (!TryFold(operand, frames, out value, depth + 1))
            return false;
        if (value is not null && length is { } n && value.Length > n)
            value = value[..n];
        return true;
    }

    /// <summary>
    /// A comparison on constants, when its result is the same under a case-sensitive and a
    /// case-insensitive collation (the database's is not known here): true or false, null for
    /// UNKNOWN; false (the method's result) when it cannot be decided.
    /// </summary>
    private static bool TryTest(BooleanExpression b, IReadOnlyList<(IList<TSqlStatement> List, int Index)> frames, out bool? result, int depth)
    {
        result = null;
        switch (b)
        {
            case BooleanParenthesisExpression p:
                return TryTest(p.Expression, frames, out result, depth + 1);
            case BooleanNotExpression n:
                if (!TryTest(n.Expression, frames, out var inner, depth + 1))
                    return false;
                result = inner is null ? null : !inner;
                return true;
            case BooleanIsNullExpression isNull:
                if (!TryFold(isNull.Expression, frames, out var v, depth + 1))
                    return false;
                result = (v is null) != isNull.IsNot;
                return true;
            case BooleanComparisonExpression { ComparisonType: BooleanComparisonType.Equals or BooleanComparisonType.NotEqualToBrackets or BooleanComparisonType.NotEqualToExclamation } c:
                if (!TryFold(c.FirstExpression, frames, out var x, depth + 1) || !TryFold(c.SecondExpression, frames, out var y, depth + 1))
                    return false;
                if (x is null || y is null)
                    return true; // UNKNOWN
                // SQL Server ignores trailing spaces in = and <>.
                string a = x.TrimEnd(' '), z = y.TrimEnd(' ');
                bool equal;
                if (string.Equals(a, z, StringComparison.Ordinal))
                    equal = true;
                else if (!string.Equals(a, z, StringComparison.OrdinalIgnoreCase))
                    equal = false;
                else
                    return false; // equal only under a case-insensitive collation
                result = c.ComparisonType == BooleanComparisonType.Equals ? equal : !equal;
                return true;
            case BooleanBinaryExpression bin:
                if (!TryTest(bin.FirstExpression, frames, out var l, depth + 1) || !TryTest(bin.SecondExpression, frames, out var r, depth + 1))
                    return false;
                result = bin.BinaryExpressionType == BooleanBinaryExpressionType.And
                    ? (l == false || r == false ? false : l == true && r == true ? true : null)
                    : (l == true || r == true ? true : l == false && r == false ? false : null);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a declared type holds text as it is (varchar, nvarchar, sysname; not char/nchar,
    /// which pad), with its length (null for max). Without a length, varchar is varchar(1) in a
    /// declaration and varchar(30) in CAST/CONVERT.
    /// </summary>
    private static bool IsVariableString(DataTypeReference? type, out int? length, int unsizedLength = 1)
    {
        length = null;
        if (type is not SqlDataTypeReference { SqlDataTypeOption: var option } sql)
            return type is UserDataTypeReference { Name.BaseIdentifier.Value: var u } && u.Equals("sysname", StringComparison.OrdinalIgnoreCase)
                   && (length = 128) is not null;
        if (option is not (SqlDataTypeOption.VarChar or SqlDataTypeOption.NVarChar))
            return false;
        if (sql.Parameters is [IntegerLiteral { Value: var n }])
            length = int.Parse(n, CultureInfo.InvariantCulture);
        else if (sql.Parameters is [MaxLiteral])
            length = null;
        else
            length = unsizedLength;
        return true;
    }
}
