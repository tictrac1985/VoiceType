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

    public static void SaveSrt(string path, IReadOnlyList<TranscriptSegment> segments)
    {
        var srt = new StringBuilder();
        var index = 1;
        foreach (var segment in segments.Where(s => !string.IsNullOrWhiteSpace(s.Text)))
        {
            srt.Append(index++.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            srt.Append(Time(segment.Start)).Append(" --> ").Append(Time(segment.End)).Append("\r\n");
            srt.Append(segment.Text.Trim()).Append("\r\n\r\n");
        }

        File.WriteAllText(path, srt.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

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

    private static string Time(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
}
