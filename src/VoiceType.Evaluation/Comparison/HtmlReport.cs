using System.Globalization;
using System.Net;
using System.Text;

namespace VoiceType.Evaluation.Comparison;

/// <summary>تقرير المقارنة كصفحة HTML عربية تُفتح في المتصفح بنقرة.</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Build(
        IReadOnlyList<FileResult> results,
        IReadOnlyList<ReportWriter.ProviderSummary> summaries,
        string? language)
    {
        var samples = results.Select(r => r.Sample).DistinctBy(s => s.Name).OrderBy(s => s.Name).ToList();
        var ranked = summaries.Where(s => s.SkipReason is null).ToList();
        var html = new StringBuilder();

        html.Append("""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="utf-8">
            <title>تقرير مقارنة مزودات التفريغ</title>
            <style>
              :root { --bg:#f7f7f5; --card:#fff; --text:#1f2328; --muted:#6b7280; --line:#e5e7eb; --good:#15803d; --bad:#b91c1c; --accent:#2563eb; }
              @media (prefers-color-scheme: dark) { :root { --bg:#16181d; --card:#1f2229; --text:#e6e6e6; --muted:#9aa0a6; --line:#30343c; --good:#4ade80; --bad:#f87171; --accent:#60a5fa; } }
              body { font-family: "Segoe UI", Tahoma, sans-serif; background:var(--bg); color:var(--text); margin:0; padding:24px; line-height:1.6; }
              .wrap { max-width:1100px; margin:auto; }
              h1 { font-size:24px; margin:0 0 4px; } h2 { font-size:19px; margin:32px 0 12px; } h3 { font-size:16px; margin:24px 0 8px; }
              .meta { color:var(--muted); margin-bottom:16px; }
              .card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:16px; overflow-x:auto; }
              table { border-collapse:collapse; width:100%; font-size:14px; }
              th, td { padding:8px 10px; border-bottom:1px solid var(--line); text-align:right; vertical-align:top; }
              th { color:var(--muted); font-weight:600; white-space:nowrap; }
              td.num { font-variant-numeric: tabular-nums; white-space:nowrap; direction:ltr; text-align:right; }
              .winner td { font-weight:600; }
              .badge { display:inline-block; background:var(--accent); color:#fff; border-radius:6px; padding:0 8px; font-size:12px; margin-inline-start:6px; }
              .ok { color:var(--good); } .fail { color:var(--bad); }
              .ref { background:var(--bg); border-inline-start:4px solid var(--accent); padding:8px 12px; border-radius:6px; margin-bottom:8px; }
              .help { color:var(--muted); font-size:13px; }
              .help li { margin:2px 0; }
            </style>
            </head>
            <body><div class="wrap">
            """);

        html.Append("<h1>تقرير مقارنة مزودات التفريغ</h1>");
        html.Append($"<div class=\"meta\">{DateTime.Now:yyyy-MM-dd HH:mm} · اللغة: {Enc(string.IsNullOrWhiteSpace(language) ? "تلقائي" : language)} · ");
        html.Append($"{samples.Count} تسجيل ({samples.Count(s => s.Reference is not null)} بنص صحيح)</div>");

        html.Append("<h2>الترتيب</h2><div class=\"card\"><table><tr><th>#</th><th>النموذج</th><th>نسبة الخطأ ↓</th><th>الكلمات الإنجليزية ↑</th><th>بعد القاموس ↑</th><th>متوسط الزمن</th><th>نجح / فشل</th><th>التكلفة</th></tr>");
        var rank = 1;
        foreach (var s in ranked)
        {
            var badge = rank == 1 && s.AverageWer is not null ? "<span class=\"badge\">الأفضل</span>" : string.Empty;
            html.Append($"<tr class=\"{(rank == 1 ? "winner" : string.Empty)}\"><td>{rank++}</td><td>{Enc(s.Label)}{badge}</td>");
            html.Append($"<td class=\"num\">{Percent(s.AverageWer)}</td><td class=\"num\">{Percent(s.LatinAccuracy)}</td><td class=\"num\">{Percent(s.LatinAccuracyAfterDictionary)}</td>");
            html.Append($"<td class=\"num\">{(s.AverageLatency is { } l ? l.TotalSeconds.ToString("0.00", Invariant) + " ث" : "—")}</td>");
            html.Append($"<td class=\"num\"><span class=\"ok\">{s.Succeeded}</span> / <span class=\"{(s.Failed > 0 ? "fail" : string.Empty)}\">{s.Failed}</span></td>");
            html.Append($"<td class=\"num\">{(s.TotalCost is { } c ? "$" + c.ToString("0.0000", Invariant) : "—")}</td></tr>");
        }

        html.Append("</table>");
        html.Append("""
            <ul class="help">
              <li><b>نسبة الخطأ:</b> نسبة الكلمات الخاطئة مقارنة بالنص الصحيح (بعد تجاهل التشكيل وأشكال الألف وعلامات الترقيم). الأقل أفضل.</li>
              <li><b>الكلمات الإنجليزية:</b> كم من الكلمات الإنجليزية كُتبت بحروف إنجليزية (Python وليس بايثون). الأعلى أفضل.</li>
              <li><b>بعد القاموس:</b> نفس المقياس بعد تصحيح البرنامج للمصطلحات التقنية المعروفة.</li>
            </ul></div>
            """);

        var skipped = summaries.Where(s => s.SkipReason is not null).ToList();
        var failures = results.Where(r => r.ErrorKind is not null && r.Provider.SkipReason is null)
            .GroupBy(r => (r.Provider.Label, r.ErrorKind, r.ErrorDetails)).ToList();
        if (skipped.Count > 0 || failures.Count > 0)
        {
            html.Append("<h2>ملاحظات</h2><div class=\"card\"><ul>");
            foreach (var s in skipped)
            {
                html.Append($"<li>{Enc(s.Label)}: تم تخطيه — {Enc(s.SkipReason!)}</li>");
            }

            foreach (var group in failures)
            {
                html.Append($"<li class=\"fail\">{Enc(group.Key.Label)}: {group.Key.ErrorKind} في {group.Count()} تسجيل — <span dir=\"ltr\">{Enc(group.Key.ErrorDetails ?? string.Empty)}</span></li>");
            }

            html.Append("</ul></div>");
        }

        html.Append("<h2>النتائج لكل تسجيل</h2>");
        foreach (var sample in samples)
        {
            html.Append($"<h3>{Enc(sample.Name)}</h3><div class=\"card\">");
            if (sample.Reference is not null)
            {
                html.Append($"<div class=\"ref\"><b>النص الصحيح:</b> {Enc(sample.Reference)}</div>");
            }

            html.Append("<table><tr><th>النموذج</th><th>الخطأ</th><th>النص</th></tr>");
            foreach (var r in results.Where(r => r.Sample.Name == sample.Name && r.Provider.SkipReason is null)
                         .OrderBy(r => r.Score?.Wer ?? double.MaxValue))
            {
                var text = r.Text is null ? $"<span class=\"fail\">✗ {r.ErrorKind}</span>" : Enc(r.Text);
                html.Append($"<tr><td>{Enc(r.Provider.Label)}</td><td class=\"num\">{Percent(r.Score?.Wer)}</td><td>{text}</td></tr>");
            }

            html.Append("</table></div>");
        }

        html.Append("</div></body></html>");
        return html.ToString();
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);

    private static string Percent(double? value) => value is { } v ? (v * 100).ToString("0.0", Invariant) + "%" : "—";
}
