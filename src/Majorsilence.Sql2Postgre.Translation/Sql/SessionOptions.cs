using Majorsilence.Sql2Postgre.Model;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Majorsilence.Sql2Postgre.Translation.Sql;

/// <summary>
/// SQL Server session options (SET …), which the converter drops: the converted code behaves as
/// SQL Server does with its defaults (us_english, ANSI_NULLS ON, …). Most change nothing a
/// PostgreSQL client sees; the ones that change the results of the statements after them are
/// reported as a Warning that says what.
/// </summary>
public static class SessionOptions
{
    /// <summary>Reports a dropped SET (S2P0204): a Warning when it changes results, otherwise an Info.</summary>
    public static void ReportDropped(TSqlStatement statement, ExpressionContext c, bool rowCountTranslated = false)
    {
        string text = ExpressionTranslator.Text(statement).Trim();
        if (Effect(statement, rowCountTranslated) is { } effect)
            c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Warning,
                $"'{text}' sets a SQL Server session option and is dropped. It changes {effect} after it, which the converted statements do as with SQL Server's default.");
        else
            c.Report(DiagnosticCodes.SessionStatementDropped, Severity.Info, $"'{text}' sets a SQL Server session option and is dropped.");
    }

    /// <summary>What the option changes in the results of the statements after it, or null.</summary>
    /// <param name="rowCountTranslated">SET ROWCOUNT is translated where it is (a routine's own result sets).</param>
    public static string? Effect(TSqlStatement statement, bool rowCountTranslated = false)
    {
        switch (statement)
        {
            case SetRowCountStatement { NumberRows: IntegerLiteral { Value: "0" } }:
                return null;
            case SetRowCountStatement when !rowCountTranslated:
                return "how many rows SELECT, INSERT, UPDATE and DELETE statements return or change";
            case SetCommandStatement set:
                var effects = set.Commands.OfType<GeneralSetCommand>().Select(g => g.CommandType switch
                {
                    GeneralSetCommandType.DateFirst => "DATEPART(weekday, …), DATETRUNC(week, …) and @@DATEFIRST (Sunday is day 1 by default)",
                    GeneralSetCommandType.DateFormat => "how strings convert to dates (mdy by default)",
                    GeneralSetCommandType.Language => "DATENAME, month and day names in conversions, and the date format (us_english by default)",
                    _ => null,
                }).OfType<string>().ToList();
                return effects.Count == 0 ? null : string.Join("; ", effects);
            case PredicateSetStatement p:
                var list = new List<string>();
                void Off(SetOptions option, string effect)
                {
                    if (!p.IsOn && (p.Options & option) != 0)
                        list.Add(effect);
                }
                void On(SetOptions option, string effect)
                {
                    if (p.IsOn && (p.Options & option) != 0)
                        list.Add(effect);
                }
                Off(SetOptions.AnsiNulls | SetOptions.AnsiDefaults, "comparisons with NULL (= NULL is true for NULL with ANSI_NULLS OFF)");
                Off(SetOptions.ConcatNullYieldsNull | SetOptions.AnsiDefaults, "string concatenation with NULL (NULL is '' with CONCAT_NULL_YIELDS_NULL OFF)");
                Off(SetOptions.AnsiWarnings | SetOptions.ArithAbort | SetOptions.AnsiDefaults, "arithmetic errors (division by zero, overflow), which give NULL instead of an error with ANSI_WARNINGS and ARITHABORT OFF");
                Off(SetOptions.QuotedIdentifier, "what double-quoted text is (a string with QUOTED_IDENTIFIER OFF; the converter reads it as a name)");
                On(SetOptions.ImplicitTransactions, "transactions (with IMPLICIT_TRANSACTIONS ON a statement opens one that only COMMIT or ROLLBACK ends)");
                On(SetOptions.NumericRoundAbort, "rounding (with NUMERIC_ROUNDABORT ON a loss of precision is an error)");
                On(SetOptions.NoExec | SetOptions.FmtOnly | SetOptions.ParseOnly, "whether the statements run at all (NOEXEC, FMTONLY and PARSEONLY skip them)");
                return list.Count == 0 ? null : string.Join("; ", list);
            default:
                return null;
        }
    }
}
