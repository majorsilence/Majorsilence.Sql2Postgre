using System.CommandLine;
using System.Text;
using System.Text.Json;
using Majorsilence.Sql2Postgre.CodeConversion;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Cli;

/// <summary><c>sql2pg code</c>: converts C# and VB.NET application code.</summary>
public sealed partial class Sql2PgCli
{
    private readonly Option<DirectoryInfo?> _codeOut = new("--out", "-o") { Description = "Where to write the converted files (the input tree is mirrored there)." };
    private readonly Option<bool> _inPlace = new("--in-place") { Description = "Rewrite the files where they are (keep them under version control)." };

    private Command CodeCommand()
    {
        var path = new Argument<string>("path") { Description = "A .cs/.vb file, a configuration file, a folder, a .csproj/.vbproj, or a .sln/.slnx." };
        var code = new Command("code", "Convert C# and VB.NET code from SQL Server (ADO.NET, Dapper, Dapper.Contrib, EF Core) to PostgreSQL (Npgsql).")
            { path, _codeOut, _inPlace, _source, _config, _targetVersion, _json };
        code.SetAction((p, ct) => Guarded(() => CodeAsync(p, p.GetValue(path)!, ct)));
        return code;
    }

    private async Task<int> CodeAsync(ParseResult p, string path, CancellationToken ct)
    {
        bool inPlace = p.GetValue(_inPlace);
        var outDir = p.GetValue(_codeOut);
        if (inPlace == (outDir is not null))
            throw new UsageException("Give either --out <folder> or --in-place.");
        var options = Options(p);
        Translation.Semantics.SemanticModel? model = null;
        if (p.GetValue(_source) is { } source)
            model = Translation.Semantics.SemanticModel.Build((await ReadSourceAsync(source, ct)).Database);
        else
            await output.WriteLineAsync("No --source: rewrites that depend on the converted schema (identity columns, procedure shapes) are reported, not made.");

        var (root, files) = CodeInput.Discover(path);
        var converter = new CodeConverter(new CodeConversionOptions(options, model));
        var results = new List<CodeFileResult>();
        foreach (var file in files)
        {
            var (text, encoding) = await ReadTextAsync(file.FullPath, ct);
            var result = converter.ConvertFile(file.RelativePath, text);
            results.Add(result);
            string target = inPlace ? file.FullPath : Path.Combine(outDir!.FullName, file.RelativePath);
            if (inPlace && !result.Changed)
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, result.NewText, encoding, ct);
        }

        int changed = results.Count(r => r.Changed);
        await output.WriteLineAsync($"{results.Count} file(s) read from {root}; {changed} changed with {results.Sum(r => r.Edits.Count)} edit(s).");
        foreach (var r in results.Where(r => r.Diagnostics.Any(d => d.Severity != Severity.Info)))
        {
            await output.WriteLineAsync($"  {r.Path}: {r.Status}");
            foreach (var d in r.Diagnostics.Where(d => d.Severity != Severity.Info))
                await output.WriteLineAsync($"    {d.Code} {d.Severity} ({d.Span?.Line},{d.Span?.Column}): {d.Message}");
        }
        if (p.GetValue(_json) is { } json)
            await File.WriteAllTextAsync(json.FullName, JsonSerializer.Serialize(results.Select(r => new
            {
                path = r.Path, language = r.Language, status = r.Status,
                edits = r.Edits.Select(e => new { start = e.Span.Start, length = e.Span.Length, e.Replacement, e.Reason }),
                diagnostics = r.Diagnostics,
            }), ConversionOptions.Json), ct);
        return results.Any(r => r.Status == ObjectStatus.Manual) ? ExitCodes.CompletedWithProblems : ExitCodes.Success;
    }

    /// <summary>A file's text and its encoding, so it is written back the same way (a BOM stays a BOM).</summary>
    private static async Task<(string Text, Encoding Encoding)> ReadTextAsync(string path, CancellationToken ct)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, ct);
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync(ct);
        var encoding = reader.CurrentEncoding;
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return (text, encoding is UTF8Encoding ? new UTF8Encoding(bom) : encoding);
    }
}
