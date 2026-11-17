using Majorsilence.Sql2Postgre.Model;
using Microsoft.CodeAnalysis.Text;

namespace Majorsilence.Sql2Postgre.CodeConversion;

/// <summary>One change to a source file: the text at <see cref="Span"/> becomes <see cref="Replacement"/>.</summary>
/// <param name="Reason">Why, in a few words, for the report.</param>
public sealed record CodeEdit(TextSpan Span, string Replacement, string Reason);

/// <summary>Two edits touch the same text: a converter bug (the file is left as it was).</summary>
public sealed class OverlappingEditsException(CodeEdit first, CodeEdit second)
    : Exception($"Edits overlap: '{first.Reason}' at {first.Span} and '{second.Reason}' at {second.Span}.");

/// <summary>Applies edits to a text. They must not overlap; edits at the same position apply in the order given.</summary>
public static class EditEngine
{
    public static string Apply(string text, IEnumerable<CodeEdit> edits)
    {
        var ordered = edits.Select((e, i) => (Edit: e, Index: i))
            .OrderBy(x => x.Edit.Span.Start).ThenBy(x => x.Edit.Span.Length).ThenBy(x => x.Index).Select(x => x.Edit).ToList();
        for (int i = 1; i < ordered.Count; i++)
        {
            var (a, b) = (ordered[i - 1], ordered[i]);
            // Touching is fine (a.End == b.Start); two inserts at one point are fine; anything else overlaps.
            if (b.Span.Start < a.Span.End || a.Span == b.Span && a.Span.Length > 0)
                throw new OverlappingEditsException(a, b);
        }
        var sb = new System.Text.StringBuilder(text.Length);
        int position = 0;
        foreach (var e in ordered)
        {
            sb.Append(text, position, e.Span.Start - position).Append(e.Replacement);
            position = e.Span.End;
        }
        return sb.Append(text, position, text.Length - position).ToString();
    }
}

/// <summary>One converted source file.</summary>
/// <param name="Path">The file's path relative to the input root.</param>
/// <param name="NewText">The converted text (the original when nothing changed or the conversion failed).</param>
public sealed record CodeFileResult(string Path, string Language, string NewText, IReadOnlyList<CodeEdit> Edits, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Changed => Edits.Count > 0;

    public ObjectStatus Status => ConvertedObject.StatusFor(Diagnostics);
}

/// <summary>Every file of a code conversion.</summary>
public sealed record CodeConversionResult(IReadOnlyList<CodeFileResult> Files)
{
    public IEnumerable<Diagnostic> Diagnostics => Files.SelectMany(f => f.Diagnostics);
}
