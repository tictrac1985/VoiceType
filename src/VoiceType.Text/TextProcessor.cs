namespace VoiceType.Text;

public enum TextMode
{
    /// <summary>التفريغ كما هو تمامًا.</summary>
    Raw,

    /// <summary>القاموس الشخصي + المصطلحات التقنية بالإنجليزي + الترقيم + المسافات. لا يغيّر المعنى.</summary>
    General,

    /// <summary>مثل General + قاموس المصطلحات التقنية، وبدون نقطة تلقائية في النهاية.</summary>
    Technical,
}

/// <summary>
/// معالجة النص بعد التفريغ. الأوضاع الثلاثة لا تغيّر معنى الكلام.
/// وضع Compose (إعادة الصياغة بـLLM) منفصل ويأتي في مرحلة لاحقة.
/// </summary>
public sealed class TextProcessor
{
    private readonly TermDictionary _personal;
    private readonly TermDictionary _technical;

    public TextProcessor(TermDictionary? personal = null, TermDictionary? technical = null)
    {
        _personal = personal ?? TermDictionary.Empty;
        _technical = technical ?? TermDictionary.LoadTechnicalDefault();
    }

    public string Process(string text, TextMode mode) => Process(text, mode, LineBreakMode.None);

    /// <summary>
    /// يعالج النص. الأمر الصوتي "سطر جديد" أو "فقرة جديدة" يبدأ سطرًا جديدًا في كل الأوضاع عدا Raw،
    /// و<paramref name="lineBreaks"/> = Sentences يجعل كل جملة في سطر.
    /// </summary>
    public string Process(string text, TextMode mode, LineBreakMode lineBreaks)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var result = text.Trim();
        if (mode == TextMode.Raw)
        {
            return result;
        }

        result = _personal.Apply(result);
        PunctuationOptions punctuation;
        switch (mode)
        {
            case TextMode.General:
                // المصطلحات التقنية المعروفة تُكتب بالإنجليزي دائمًا (بايثون ← Python)، خصوصًا مع النموذج المحلي
                result = _technical.Apply(result);
                punctuation = new PunctuationOptions();
                break;
            case TextMode.Technical:
                result = _technical.Apply(result);
                punctuation = new PunctuationOptions { AddTerminalPunctuation = false };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var parts = LineBreaks.SplitCommands(result)
            .Select(part =>
            {
                var line = ArabicPunctuation.Normalize(part.Text, punctuation);
                if (lineBreaks == LineBreakMode.Sentences)
                {
                    line = LineBreaks.BreakSentences(line);
                }

                return (line, part.Separator);
            });
        return LineBreaks.Join(parts);
    }
}
