using System.Globalization;
using System.Net;
using System.Text;
using Majorsilence.Sql2Postgre.Model;

namespace Majorsilence.Sql2Postgre.Cli;

/// <summary>
/// The assessment as one self-contained HTML file: no external scripts, styles or fonts, so it
/// can be mailed or archived. A summary of objects by kind and status, the effort estimate, the
/// most frequent diagnostics, and a sortable, filterable table of every object.
/// </summary>
public static class HtmlReport
{
    public static string Render(string database, int targetVersion, SourceReadResult read, ConversionResult result, DateTimeOffset generated)
    {
        var objects = result.Objects.Where(o => o.Kind is not (ObjectKind.Schema or ObjectKind.Extension or ObjectKind.Collation)).ToList();
        var all = read.Diagnostics.Concat(result.Diagnostics).Concat(result.Objects.SelectMany(o => o.Diagnostics)).ToList();
        int effort = DiagnosticCodes.Effort(all);
        var statuses = Enum.GetValues<ObjectStatus>();

        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n")
          .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
          .Append("<title>sql2pg assessment: ").Append(E(database)).Append("</title>\n<style>\n").Append(Css).Append("</style>\n</head>\n<body>\n<main>\n");
        sb.Append("<h1>sql2pg assessment</h1>\n<p class=\"meta\">").Append(E(database)).Append(" → PostgreSQL ")
          .Append(targetVersion.ToString(CultureInfo.InvariantCulture)).Append(" · ")
          .Append(E(generated.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", CultureInfo.InvariantCulture))).Append("</p>\n");

        // Cards.
        sb.Append("<section class=\"cards\">\n");
        Card(sb, "Objects", objects.Count.ToString(CultureInfo.InvariantCulture), "");
        foreach (var s in statuses)
            Card(sb, Label(s), objects.Count(o => o.Status == s).ToString(CultureInfo.InvariantCulture), s.ToString().ToLowerInvariant());
        Card(sb, "Effort (points)", effort.ToString(CultureInfo.InvariantCulture), "effort");
        sb.Append("</section>\n<p class=\"note\">Effort adds up each diagnostic's weight: Info 0, Warning 1, Error 5, and more for code a person must rewrite (encrypted or CLR objects, dynamic DDL). See docs/diagnostics.md.</p>\n");

        // By kind.
        sb.Append("<h2>By kind</h2>\n<div class=\"scroll\"><table class=\"grid\"><thead><tr><th>Kind</th>");
        foreach (var s in statuses)
            sb.Append("<th class=\"num\">").Append(Label(s)).Append("</th>");
        sb.Append("<th class=\"num\">Effort</th></tr></thead><tbody>\n");
        foreach (var kind in objects.Select(o => o.Kind).Distinct().Order())
        {
            var ofKind = objects.Where(o => o.Kind == kind).ToList();
            sb.Append("<tr><td>").Append(kind).Append("</td>");
            foreach (var s in statuses)
                sb.Append("<td class=\"num\">").Append(ofKind.Count(o => o.Status == s)).Append("</td>");
            sb.Append("<td class=\"num\">").Append(DiagnosticCodes.Effort(ofKind.SelectMany(o => o.Diagnostics))).Append("</td></tr>\n");
        }
        sb.Append("</tbody></table></div>\n");

        // Diagnostics.
        if (all.Count > 0)
        {
            sb.Append("<h2>Diagnostics</h2>\n<div class=\"scroll\"><table class=\"grid\"><thead><tr><th>Code</th><th>Name</th><th>Severity</th><th class=\"num\">Count</th><th class=\"num\">Effort</th></tr></thead><tbody>\n");
            foreach (var g in all.GroupBy(d => (d.Code, d.Severity)).OrderByDescending(g => g.Sum(d => DiagnosticCodes.Weight(d.Code, d.Severity))).ThenByDescending(g => g.Count()).ThenBy(g => g.Key.Code, StringComparer.Ordinal))
                sb.Append("<tr><td><code>").Append(g.Key.Code).Append("</code></td><td>").Append(E(Sql2PgCli.CodeName(g.Key.Code)))
                  .Append("</td><td><span class=\"sev ").Append(g.Key.Severity.ToString().ToLowerInvariant()).Append("\">").Append(g.Key.Severity).Append("</span></td><td class=\"num\">")
                  .Append(g.Count()).Append("</td><td class=\"num\">").Append(g.Sum(d => DiagnosticCodes.Weight(d.Code, d.Severity))).Append("</td></tr>\n");
            sb.Append("</tbody></table></div>\n");
        }

        // Objects.
        sb.Append("<h2>Objects</h2>\n<input id=\"filter\" type=\"search\" placeholder=\"Filter by name, kind, status or code\" aria-label=\"Filter objects\">\n")
          .Append("<div class=\"scroll\"><table id=\"objects\" class=\"grid sortable\"><thead><tr>")
          .Append("<th data-type=\"text\">Object</th><th data-type=\"text\">Kind</th><th data-type=\"text\">Status</th><th data-type=\"num\" class=\"num\">Effort</th><th data-type=\"text\">Diagnostics</th>")
          .Append("</tr></thead><tbody>\n");
        foreach (var o in objects.OrderByDescending(o => DiagnosticCodes.Effort(o.Diagnostics)).ThenBy(o => o.TargetName, StringComparer.Ordinal))
        {
            sb.Append("<tr><td><code>").Append(E(o.Source.ToString())).Append("</code><div class=\"target\">").Append(E(o.TargetName)).Append("</div></td><td>")
              .Append(o.Kind).Append("</td><td><span class=\"status ").Append(o.Status.ToString().ToLowerInvariant()).Append("\">").Append(Label(o.Status))
              .Append("</span></td><td class=\"num\">").Append(DiagnosticCodes.Effort(o.Diagnostics)).Append("</td><td>");
            foreach (var d in o.Diagnostics.Where(d => d.Severity != Severity.Info))
                sb.Append("<details><summary><code>").Append(d.Code).Append("</code> ").Append(E(Sql2PgCli.CodeName(d.Code))).Append("</summary><p>")
                  .Append(E(d.Message)).Append("</p></details>");
            int info = o.Diagnostics.Count(d => d.Severity == Severity.Info);
            if (info > 0)
                sb.Append("<span class=\"info\">").Append(info).Append(" info</span>");
            sb.Append("</td></tr>\n");
        }
        sb.Append("</tbody></table></div>\n</main>\n<script>\n").Append(Script).Append("</script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string label, string value, string kind) =>
        sb.Append("<div class=\"card ").Append(kind).Append("\"><div class=\"value\">").Append(E(value)).Append("</div><div class=\"label\">").Append(E(label)).Append("</div></div>\n");

    private static string Label(ObjectStatus s) => s switch
    {
        ObjectStatus.ConvertedWithWarnings => "With warnings",
        _ => s.ToString(),
    };

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private const string Css = """
        :root { color-scheme: light dark; --bg: #ffffff; --fg: #1d1d1f; --muted: #6b6b73; --line: #e2e2e7; --card: #f6f6f8;
          --ok: #1f7a3a; --warn: #9a6200; --bad: #b3261e; --skip: #6b6b73; --accent: #2f5bd3; }
        @media (prefers-color-scheme: dark) { :root { --bg: #16161a; --fg: #ececf1; --muted: #a0a0aa; --line: #2c2c33; --card: #1f1f25;
          --ok: #5cc982; --warn: #e3a948; --bad: #f2867d; --skip: #a0a0aa; --accent: #8aa8ff; } }
        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--fg); font: 15px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
        main { max-width: 1200px; margin: 0 auto; padding: 24px 16px 48px; }
        h1 { font-size: 1.6rem; margin: 0 0 4px; } h2 { font-size: 1.15rem; margin: 32px 0 8px; }
        .meta, .note, .target, .info { color: var(--muted); } .note { font-size: .85rem; } .target { font-size: .8rem; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin-top: 16px; }
        .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 12px 14px; }
        .card .value { font-size: 1.6rem; font-weight: 600; font-variant-numeric: tabular-nums; } .card .label { color: var(--muted); font-size: .85rem; }
        .card.converted .value { color: var(--ok); } .card.convertedwithwarnings .value { color: var(--warn); }
        .card.manual .value { color: var(--bad); } .card.skipped .value { color: var(--skip); } .card.effort .value { color: var(--accent); }
        .scroll { overflow-x: auto; }
        table.grid { border-collapse: collapse; width: 100%; } .grid th, .grid td { text-align: left; padding: 6px 10px; border-bottom: 1px solid var(--line); vertical-align: top; }
        .grid th { font-weight: 600; white-space: nowrap; } .num { text-align: right !important; font-variant-numeric: tabular-nums; }
        .sortable th { cursor: pointer; user-select: none; } .sortable th[aria-sort="ascending"]::after { content: " ▲"; } .sortable th[aria-sort="descending"]::after { content: " ▼"; }
        .status, .sev { font-size: .8rem; padding: 1px 8px; border-radius: 999px; border: 1px solid currentColor; white-space: nowrap; }
        .status.converted, .sev.info { color: var(--ok); } .status.convertedwithwarnings, .sev.warning { color: var(--warn); }
        .status.manual, .sev.error { color: var(--bad); } .status.skipped { color: var(--skip); }
        details { margin: 0 0 2px; } details summary { cursor: pointer; } details p { margin: 4px 0 8px; font-size: .85rem; color: var(--muted); }
        code { font-family: ui-monospace, "Cascadia Mono", Consolas, monospace; font-size: .85em; }
        #filter { width: 100%; max-width: 420px; padding: 8px 10px; margin: 0 0 8px; border: 1px solid var(--line); border-radius: 8px; background: var(--card); color: var(--fg); }
        """;

    private const string Script = """
        (function () {
          var table = document.getElementById('objects');
          var body = table.tBodies[0];
          table.querySelectorAll('th').forEach(function (th, index) {
            th.addEventListener('click', function () {
              var ascending = th.getAttribute('aria-sort') !== 'ascending';
              table.querySelectorAll('th').forEach(function (h) { h.removeAttribute('aria-sort'); });
              th.setAttribute('aria-sort', ascending ? 'ascending' : 'descending');
              var numeric = th.dataset.type === 'num';
              var rows = Array.prototype.slice.call(body.rows);
              rows.sort(function (a, b) {
                var x = a.cells[index].textContent.trim(), y = b.cells[index].textContent.trim();
                var c = numeric ? (parseFloat(x) || 0) - (parseFloat(y) || 0) : x.localeCompare(y);
                return ascending ? c : -c;
              });
              rows.forEach(function (r) { body.appendChild(r); });
            });
          });
          document.getElementById('filter').addEventListener('input', function (e) {
            var q = e.target.value.toLowerCase();
            Array.prototype.forEach.call(body.rows, function (r) {
              r.style.display = r.textContent.toLowerCase().indexOf(q) >= 0 ? '' : 'none';
            });
          });
        })();
        """;
}
