using System.Globalization;
using System.Text;

namespace VoiceType.Evaluation.Comparison;

/// <summary>
/// يكتب نتائج المقارنة: report.md (ملخص بالعربية) + results.csv (يفتح في Excel) + نصوص كل مزود.
/// </summary>
public static class ReportWriter
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public sealed record ProviderSummary(
        string Label,
        string DefinitionId,
        string? Model,
        int Succeeded,
        int Failed,
        double? AverageWer,
        double? AverageCer,
        double? LatinAccuracy,
        double? LatinAccuracyAfterDictionary,
        TimeSpan? AverageLatency,
        double? TotalCost,
        string? SkipReason);

    public static IReadOnlyList<ProviderSummary> Summarize(IReadOnlyList<FileResult> results) =>
        results
            .GroupBy(r => r.Provider.Label)
            .Select(group =>
            {
                var ok = group.Where(r => r.ErrorKind is null).ToList();
                var scored = ok.Where(r => r.Score is not null).ToList();
                var latinTotal = scored.Sum(r => r.Score!.LatinTermsTotal);
                var first = group.First().Provider;

                return new ProviderSummary(
                    group.Key,
                    first.DefinitionId,
                    first.Model,
                    ok.Count,
                    group.Count() - ok.Count,
                    WeightedRate(scored.Sum(r => r.Score!.WordErrors), scored.Sum(r => r.Score!.ReferenceWords)),
                    WeightedRate(scored.Sum(r => r.Score!.CharErrors), scored.Sum(r => r.Score!.ReferenceChars)),
                    latinTotal == 0 ? null : (double)scored.Sum(r => r.Score!.LatinTermsFound) / latinTotal,
                    latinTotal == 0 ? null : (double)scored.Sum(r => r.TechnicalScore!.LatinTermsFound) / latinTotal,
                    ok.Count == 0 ? null : TimeSpan.FromMilliseconds(ok.Average(r => r.Elapsed.TotalMilliseconds)),
                    ok.Any(r => r.Cost is not null) ? ok.Sum(r => r.Cost ?? 0) : null,
                    first.SkipReason);
            })
            .OrderBy(s => s.AverageWer ?? double.MaxValue)
            .ThenBy(s => s.AverageLatency ?? TimeSpan.MaxValue)
            .ToList();

    public static void Write(string outputDirectory, IReadOnlyList<FileResult> results, string? language)
    {
        Directory.CreateDirectory(outputDirectory);
        var summaries = Summarize(results);

        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), BuildMarkdown(results, summaries, language), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, "report.html"), HtmlReport.Build(results, summaries, language), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(outputDirectory, "results.csv"), BuildCsv(results), new UTF8Encoding(true));

        foreach (var result in results.Where(r => r.Text is not null))
        {
            var folder = Path.Combine(outputDirectory, "transcripts", SafeName(result.Provider.Label));
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, Path.ChangeExtension(result.Sample.Name, ".txt")), result.Text, new UTF8Encoding(false));
        }
    }

    private static string BuildMarkdown(IReadOnlyList<FileResult> results, IReadOnlyList<ProviderSummary> summaries, string? language)
    {
        var samples = results.Select(r => r.Sample).DistinctBy(s => s.Name).OrderBy(s => s.Name).ToList();
        var md = new StringBuilder();

        md.AppendLine("# تقرير مقارنة مزودات التفريغ");
        md.AppendLine();
        md.AppendLine($"- التاريخ: {DateTime.Now:yyyy-MM-dd HH:mm}");
        md.AppendLine($"- اللغة: {(string.IsNullOrWhiteSpace(language) ? "تلقائي" : language)}");
        md.AppendLine($"- عدد المقاطع: {samples.Count} (منها {samples.Count(s => s.Reference is not null)} بنص مرجعي)");
        md.AppendLine();
        md.AppendLine("## الملخص (الأفضل أولًا)");
        md.AppendLine();
        md.AppendLine("| # | المزود | نجح | فشل | WER ↓ | CER ↓ | المصطلحات الإنجليزية ↑ | بعد القاموس ↑ | متوسط الزمن | التكلفة |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

        var rank = 1;
        foreach (var s in summaries.Where(s => s.SkipReason is null))
        {
            md.AppendLine(
                $"| {rank++} | {Escape(s.Label)} | {s.Succeeded} | {s.Failed} | {Percent(s.AverageWer)} | {Percent(s.AverageCer)} | " +
                $"{Percent(s.LatinAccuracy)} | {Percent(s.LatinAccuracyAfterDictionary)} | " +
                $"{(s.AverageLatency is { } l ? l.TotalSeconds.ToString("0.00", Invariant) + " ث" : "—")} | " +
                $"{(s.TotalCost is { } c ? "$" + c.ToString("0.0000", Invariant) : "—")} |");
        }

        var skipped = summaries.Where(s => s.SkipReason is not null).ToList();
        if (skipped.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("**تم تخطيها:**");
            foreach (var s in skipped)
            {
                md.AppendLine($"- {Escape(s.Label)}: {Escape(s.SkipReason!)}");
            }
        }

        md.AppendLine();
        md.AppendLine("**قراءة الجدول:**");
        md.AppendLine("- **WER / CER:** نسبة خطأ الكلمات / الحروف بعد توحيد الكتابة (التشكيل، أشكال الألف، الترقيم). الأقل أفضل.");
        md.AppendLine("- **المصطلحات الإنجليزية:** نسبة الكلمات الإنجليزية في النص المرجعي التي كُتبت بحروف إنجليزية في التفريغ. الأعلى أفضل.");
        md.AppendLine("- **بعد القاموس:** نفس المقياس بعد تطبيق قاموس المصطلحات التقنية (وضع Technical).");
        md.AppendLine("- **التكلفة:** تظهر فقط للمزودات التي تعيدها في الرد (مثل OpenRouter).");

        var failures = results.Where(r => r.ErrorKind is not null && r.Provider.SkipReason is null).GroupBy(r => (r.Provider.Label, r.ErrorKind, r.ErrorDetails)).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## الأخطاء");
            md.AppendLine();
            foreach (var group in failures)
            {
                md.AppendLine($"- **{Escape(group.Key.Label)}** — {group.Key.ErrorKind} ({group.Count()} مقطع): `{Escape(group.Key.ErrorDetails ?? string.Empty)}`");
            }
        }

        md.AppendLine();
        md.AppendLine("## التفاصيل لكل مقطع");

        foreach (var sample in samples)
        {
            md.AppendLine();
            md.AppendLine($"### {Escape(sample.Name)}");
            md.AppendLine();
            if (sample.Reference is not null)
            {
                md.AppendLine($"> **النص الصحيح:** {Escape(sample.Reference)}");
                md.AppendLine();
            }

            md.AppendLine("| المزود | WER | النص |");
            md.AppendLine("|---|---|---|");
            foreach (var result in results.Where(r => r.Sample.Name == sample.Name && r.Provider.SkipReason is null)
                         .OrderBy(r => r.Score?.Wer ?? double.MaxValue))
            {
                var text = result.Text is null ? $"❌ {result.ErrorKind}" : Escape(result.Text);
                md.AppendLine($"| {Escape(result.Provider.Label)} | {Percent(result.Score?.Wer)} | {text} |");
            }
        }

        return md.ToString();
    }

    private static string BuildCsv(IReadOnlyList<FileResult> results)
    {
        var csv = new StringBuilder();
        csv.AppendLine("provider,definition,model,file,ok,latency_s,wer,cer,latin_found,latin_total,latin_found_after_dictionary,cost,error,text,technical_text,reference");
        foreach (var r in results)
        {
            csv.AppendLine(string.Join(',',
                Csv(r.Provider.Label),
                Csv(r.Provider.DefinitionId),
                Csv(r.Provider.Model),
                Csv(r.Sample.Name),
                r.ErrorKind is null ? "1" : "0",
                r.ErrorKind is null ? r.Elapsed.TotalSeconds.ToString("0.000", Invariant) : string.Empty,
                r.Score?.Wer.ToString("0.0000", Invariant) ?? string.Empty,
                r.Score?.Cer.ToString("0.0000", Invariant) ?? string.Empty,
                r.Score?.LatinTermsFound.ToString(Invariant) ?? string.Empty,
                r.Score?.LatinTermsTotal.ToString(Invariant) ?? string.Empty,
                r.TechnicalScore?.LatinTermsFound.ToString(Invariant) ?? string.Empty,
                r.Cost?.ToString("0.000000", Invariant) ?? string.Empty,
                Csv(r.ErrorKind is null ? null : $"{r.ErrorKind}: {r.ErrorDetails}"),
                Csv(r.Text),
                Csv(r.TechnicalText),
                Csv(r.Sample.Reference)));
        }

        return csv.ToString();
    }

    private static double? WeightedRate(int errors, int total) => total == 0 ? null : (double)errors / total;

    private static string Percent(double? value) => value is { } v ? (v * 100).ToString("0.0", Invariant) + "%" : "—";

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var cleaned = value.ReplaceLineEndings(" ");
        return cleaned.IndexOfAny([',', '"']) >= 0 ? $"\"{cleaned.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : cleaned;
    }

    private static string Escape(string value) => value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal);

    private static string SafeName(string value) =>
        string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' or '\\' ? '_' : c));
}
