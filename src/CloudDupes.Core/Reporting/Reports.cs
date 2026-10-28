using System.Globalization;
using System.Net;
using System.Text;
using CloudDupes.Core.Planning;

namespace CloudDupes.Core.Reporting;

public static class CsvReport
{
    public static void Write(DuplicatePlan plan, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("group,kind,needs_review,action,provider,path,size,modified,id,web_url");
        foreach (var g in plan.Groups)
            foreach (var f in g.Files)
                sb.AppendLine(string.Join(',',
                    g.Number, g.Kind, g.NeedsReview, f.Action, f.Provider, Escape(f.Path), f.Size,
                    f.Modified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), Escape(f.Id), Escape(f.WebUrl ?? "")));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}

public static class HtmlReport
{
    /// <param name="thumbnail">Returns a small JPEG for a file key, or null.</param>
    public static void Write(DuplicatePlan plan, string path, Func<PlanFile, byte[]?> thumbnail)
    {
        var deletions = PlanExecutor.Targets(plan);
        var sb = new StringBuilder();
        sb.Append($$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>CloudDupes Report</title>
            <style>
            :root { --bg:#f7f7f8; --card:#fff; --text:#1d1d1f; --muted:#6b6b73; --border:#e2e2e6;
                    --keep:#1f7a3d; --keep-bg:#e3f4e8; --del:#b42318; --del-bg:#fde8e6; --review:#8a5a00; --review-bg:#fff3d6; }
            @media (prefers-color-scheme: dark) { :root { --bg:#141416; --card:#1e1e21; --text:#ececef; --muted:#9a9aa3; --border:#333338;
                    --keep:#6fd38f; --keep-bg:#173322; --del:#ff8a80; --del-bg:#3a1b19; --review:#f3c45a; --review-bg:#3a2e12; } }
            * { box-sizing:border-box } body { margin:0; padding:16px; background:var(--bg); color:var(--text); font:14px/1.45 system-ui, sans-serif }
            h1 { font-size:22px; margin:0 0 4px } .muted { color:var(--muted) }
            .summary { display:flex; flex-wrap:wrap; gap:12px; margin:16px 0 }
            .stat { background:var(--card); border:1px solid var(--border); border-radius:10px; padding:10px 14px }
            .stat b { display:block; font-size:20px }
            .filters { margin:8px 0 16px; display:flex; gap:8px; flex-wrap:wrap }
            .filters button { background:var(--card); color:var(--text); border:1px solid var(--border); border-radius:999px; padding:4px 12px; cursor:pointer }
            .filters button.on { border-color:var(--text) }
            .group { background:var(--card); border:1px solid var(--border); border-radius:12px; margin:0 0 12px; padding:12px }
            .group h2 { font-size:14px; margin:0 0 8px; display:flex; gap:8px; align-items:center; flex-wrap:wrap }
            .tag { font-size:11px; padding:1px 8px; border-radius:999px; border:1px solid var(--border) }
            .tag.review { background:var(--review-bg); color:var(--review); border-color:transparent }
            .file { display:flex; gap:10px; align-items:center; padding:6px 0; border-top:1px solid var(--border); min-width:0 }
            .file img, .file .ph { width:64px; height:64px; object-fit:cover; border-radius:6px; flex:none; background:var(--border) }
            .info { min-width:0; flex:1 } .path { overflow-wrap:anywhere } .path a { color:inherit }
            .act { font-size:12px; font-weight:600; padding:2px 8px; border-radius:6px; flex:none }
            .act.Keep { background:var(--keep-bg); color:var(--keep) } .act.Delete { background:var(--del-bg); color:var(--del) }
            </style></head><body>
            <h1>Duplicate report</h1>
            <div class="muted">Generated {{plan.CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}} · edit <code>plan.json</code> to change what gets deleted, then run <code>clouddupes apply</code>.</div>
            <div class="summary">
              <div class="stat"><b>{{plan.Groups.Count:N0}}</b>groups</div>
              <div class="stat"><b>{{deletions.Count:N0}}</b>files proposed for trash</div>
              <div class="stat"><b>{{FormatSize(deletions.Sum(f => f.Size))}}</b>reclaimable</div>
            </div>
            <div class="filters" id="filters">
              <button data-kind="all" class="on">All</button><button data-kind="Exact">Exact</button>
              <button data-kind="Similar">Similar</button><button data-kind="NameSize">Name + size</button>
            </div>
            """);

        foreach (var g in plan.Groups)
        {
            sb.Append($"""<section class="group" data-kind="{g.Kind}"><h2>#{g.Number} <span class="tag">{KindLabel(g.Kind)}</span>""");
            if (g.NeedsReview) sb.Append("""<span class="tag review">review</span>""");
            sb.Append($"""<span class="muted">{g.Files.Count} files · {FormatSize(g.Files[0].Size)}</span></h2>""");
            foreach (var f in g.Files)
            {
                var thumb = f.IsImage ? thumbnail(f) : null;
                sb.Append("<div class=\"file\">");
                sb.Append(thumb is null
                    ? "<div class=\"ph\"></div>"
                    : $"<img loading=\"lazy\" alt=\"\" src=\"data:image/jpeg;base64,{Convert.ToBase64String(thumb)}\">");
                var name = Enc(f.Path);
                var link = f.WebUrl is null ? name : $"<a href=\"{Enc(f.WebUrl)}\" target=\"_blank\" rel=\"noopener\">{name}</a>";
                sb.Append($"""<div class="info"><div class="path">{link}</div><div class="muted">{ProviderLabel(f.Provider)} · {FormatSize(f.Size)} · {f.Modified.ToLocalTime():yyyy-MM-dd HH:mm}</div></div>""");
                sb.Append($"""<span class="act {f.Action}">{f.Action}</span></div>""");
            }
            sb.Append("</section>");
        }

        sb.Append("""
            <script>
            document.getElementById('filters').addEventListener('click', e => {
              const kind = e.target.dataset && e.target.dataset.kind; if (!kind) return;
              document.querySelectorAll('#filters button').forEach(b => b.classList.toggle('on', b === e.target));
              document.querySelectorAll('.group').forEach(g => g.hidden = kind !== 'all' && g.dataset.kind !== kind);
            });
            </script></body></html>
            """);
        File.WriteAllText(path, sb.ToString());
    }

    static string Enc(string s) => WebUtility.HtmlEncode(s);

    static string KindLabel(Models.MatchKind kind) => kind switch
    {
        Models.MatchKind.Exact => "Exact",
        Models.MatchKind.Similar => "Similar photo",
        _ => "Name + size",
    };

    static string ProviderLabel(Models.CloudProviderKind p) => p == Models.CloudProviderKind.OneDrive ? "OneDrive" : "Google Drive";

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}
