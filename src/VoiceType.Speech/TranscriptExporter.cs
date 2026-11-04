using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using VoiceType.Core.Speech;

namespace VoiceType.Speech;

/// <summary>حفظ التفريغ كملف نصي أو Word أو ترجمة SRT، مع الحفاظ على العربية واتجاه النص.</summary>
public static class TranscriptExporter
{
    public static void SaveText(string path, string text) =>
        File.WriteAllText(path, text.Replace("\n", "\r\n", StringComparison.Ordinal), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    /// <summary>أطوال سطر الترجمة: عادي للفيديو، قصير للريلز والشورتس، طويل للمحاضرات.</summary>
    public const int CaptionNormal = 42;

    public const int CaptionShort = 24;

    public const int CaptionLong = 60;

    public static void SaveSrt(string path, IReadOnlyList<TranscriptSegment> segments, int maxChars = 0) =>
        File.WriteAllText(path, ToSrt(segments, maxChars), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static string ToSrt(IReadOnlyList<TranscriptSegment> segments, int maxChars = 0)
    {
        var srt = new StringBuilder();
        var index = 1;
        foreach (var (segment, text) in Cues(segments, maxChars))
        {
            srt.Append(index++.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            srt.Append(Time(segment.Start)).Append(" --> ").Append(Time(segment.End)).Append("\r\n");
            srt.Append(text).Append("\r\n\r\n");
        }

        return srt.ToString();
    }

    /// <summary>ترجمة WebVTT (يوتيوب، المتصفحات، أغلب مشغلات الفيديو).</summary>
    public static void SaveVtt(string path, IReadOnlyList<TranscriptSegment> segments, int maxChars = 0) =>
        File.WriteAllText(path, ToVtt(segments, maxChars), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public static string ToVtt(IReadOnlyList<TranscriptSegment> segments, int maxChars = 0)
    {
        var vtt = new StringBuilder("WEBVTT\n\n");
        foreach (var (segment, text) in Cues(segments, maxChars))
        {
            vtt.Append(Time(segment.Start, '.')).Append(" --> ").Append(Time(segment.End, '.')).Append('\n');
            // في VTT العلامات < و & ليها معنى، و --> بتلخبط الوقت
            vtt.Append(text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append("\n\n");
        }

        return vtt.ToString();
    }

    /// <summary>JSON فيه كل حاجة: النص، والجمل بأوقاتها، ومين قالها. مناسب للبرامج التانية.</summary>
    public static string ToJson(string title, string text, IReadOnlyList<TranscriptSegment> segments, string? engine = null, TimeSpan? duration = null)
    {
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["title"] = title,
            ["engine"] = engine,
            ["durationSeconds"] = duration is { } d ? Math.Round(d.TotalSeconds, 2) : null,
            ["speakers"] = segments.Where(x => x.Speaker is >= 0).Select(x => x.Speaker).Distinct().Count(),
            ["text"] = text,
            ["segments"] = new System.Text.Json.Nodes.JsonArray(segments.Select(x => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["start"] = Math.Round(x.Start.TotalSeconds, 2),
                ["end"] = Math.Round(x.End.TotalSeconds, 2),
                ["speaker"] = x.Speaker is { } who ? TranscriptSegment.SpeakerName(who) : null,
                ["text"] = x.Text,
            }).ToArray()),
        };
        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    public static void SaveJson(string path, string title, string text, IReadOnlyList<TranscriptSegment> segments, string? engine = null, TimeSpan? duration = null) =>
        File.WriteAllText(path, ToJson(title, text, segments, engine, duration), new UTF8Encoding(false));

    /// <summary>
    /// صفحة HTML من اليمين للشمال، جاهزة للطباعة وللتحويل لـ PDF: كل دور كلام في فقرة،
    /// وجنبه اسم المتكلم ووقته لو معروفين.
    /// </summary>
    public static string ToHtml(string title, string text, IReadOnlyList<TranscriptSegment> segments, string? subtitle = null)
    {
        static string E(string value) => System.Net.WebUtility.HtmlEncode(value);
        var body = new StringBuilder();
        var hasSpeakers = segments.Any(x => x.Speaker is >= 0);
        if (hasSpeakers)
        {
            int? current = null;
            StringBuilder? paragraph = null;
            TimeSpan start = default;
            void Close()
            {
                if (paragraph is { Length: > 0 })
                {
                    body.Append("<p><span class=\"who\">").Append(E(current is { } c ? TranscriptSegment.SpeakerName(c) : "")).Append("</span>")
                        .Append("<span class=\"at\">").Append(Clock(start)).Append("</span> ")
                        .Append(E(paragraph.ToString())).Append("</p>\n");
                }
            }

            foreach (var segment in segments.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
            {
                if (paragraph is null || segment.Speaker != current)
                {
                    Close();
                    current = segment.Speaker;
                    start = segment.Start;
                    paragraph = new StringBuilder();
                }
                else
                {
                    paragraph.Append(' ');
                }

                paragraph.Append(segment.Text.Trim());
            }

            Close();
        }
        else
        {
            foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                body.Append("<p>").Append(E(paragraph).Replace("\n", "<br>", StringComparison.Ordinal)).Append("</p>\n");
            }
        }

        return $$"""
            <!DOCTYPE html>
            <html lang="ar" dir="rtl">
            <head>
            <meta charset="utf-8">
            <title>{{E(title)}}</title>
            <style>
              body { font-family: "Segoe UI", Tahoma, Arial, sans-serif; line-height: 1.9; color: #1d1d1f; background: #fff; max-width: 820px; margin: 40px auto; padding: 0 24px; font-size: 16px; }
              h1 { font-size: 24px; margin: 0 0 4px; }
              .meta { color: #6e6e73; font-size: 13px; margin-bottom: 28px; border-bottom: 1px solid #e5e5ea; padding-bottom: 12px; }
              p { margin: 0 0 14px; text-align: justify; }
              .who { font-weight: 700; color: #8a5a00; margin-left: 8px; }
              .at { color: #8e8e93; font-size: 12px; direction: ltr; unicode-bidi: isolate; margin-left: 6px; }
              @media print { body { margin: 0 auto; } }
            </style>
            </head>
            <body>
            <h1>{{E(title)}}</h1>
            <div class="meta">{{E(subtitle ?? "")}}</div>
            {{body}}
            </body>
            </html>
            """;
    }

    public static void SaveHtml(string path, string title, string text, IReadOnlyList<TranscriptSegment> segments, string? subtitle = null) =>
        File.WriteAllText(path, ToHtml(title, text, segments, subtitle), new UTF8Encoding(false));

    /// <summary>
    /// سطور الترجمة: الجملة الطويلة بتتقسم لسطور قصيرة على قد الحروف المطلوبة (بالكلمة، من غير تقطيع كلمة)،
    /// والوقت بيتوزع عليهم على قد طولهم. لما المتكلم يتغير، اسمه بيظهر في أول السطر.
    /// </summary>
    public static IReadOnlyList<(TranscriptSegment Segment, string Text)> Cues(IReadOnlyList<TranscriptSegment> segments, int maxChars = 0)
    {
        var cues = new List<(TranscriptSegment, string)>();
        int? lastSpeaker = null;
        foreach (var segment in segments.Where(x => !string.IsNullOrWhiteSpace(x.Text)))
        {
            // سطر فاضي جوه الجملة بيبوّظ ملف الترجمة (السطر الفاضي معناه «آخر الجملة»)، فنخلي الكلام كله بمسافات عادية
            var clean = System.Text.RegularExpressions.Regex.Replace(segment.Text, @"\s+", " ").Trim();
            var pieces = maxChars > 0 ? SplitByLength(clean, maxChars) : [clean];
            var total = pieces.Sum(p => p.Length);
            var cursor = segment.Start;
            var span = segment.End - segment.Start;
            for (var i = 0; i < pieces.Count; i++)
            {
                var share = total == 0 ? 1.0 / pieces.Count : pieces[i].Length / (double)total;
                var end = i == pieces.Count - 1 ? segment.End : cursor + (span * share);
                var text = pieces[i];
                if (i == 0 && segment.Speaker is { } who && who != lastSpeaker)
                {
                    text = $"[{TranscriptSegment.SpeakerName(who)}] {text}";
                    lastSpeaker = who;
                }

                cues.Add((segment with { Start = cursor, End = end }, text));
                cursor = end;
            }
        }

        return cues;
    }

    private static List<string> SplitByLength(string text, int maxChars)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > maxChars)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        return lines;
    }

    private static string Clock(TimeSpan value) =>
        value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes:00}:{value.Seconds:00}";

    public static void SaveDocx(string path, string title, string text)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        var body = new Body();

        body.Append(CreateParagraph(title, bold: true, size: 32));
        foreach (var paragraph in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            body.Append(CreateParagraph(paragraph, bold: false, size: 24));
        }

        body.Append(new SectionProperties(new BiDi()));
        main.Document = new Document(body);
        main.Document.Save();
    }

    private static Paragraph CreateParagraph(string text, bool bold, int size)
    {
        var runProperties = new RunProperties(
            new RunFonts { Ascii = "Arial", HighAnsi = "Arial", ComplexScript = "Arial" },
            new RightToLeftText(),
            new FontSize { Val = size.ToString(CultureInfo.InvariantCulture) },
            new FontSizeComplexScript { Val = size.ToString(CultureInfo.InvariantCulture) });
        if (bold)
        {
            runProperties.Append(new Bold(), new BoldComplexScript());
        }

        return new Paragraph(
            new ParagraphProperties(
                new BiDi(),
                new SpacingBetweenLines { After = "200", Line = "360", LineRule = LineSpacingRuleValues.Auto }),
            new Run(runProperties, new DocumentFormat.OpenXml.Wordprocessing.Text(text) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static string Time(TimeSpan value, char separator = ',') =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}";
}
