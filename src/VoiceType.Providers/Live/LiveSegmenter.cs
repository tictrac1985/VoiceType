using System.Text;

namespace VoiceType.Providers.Live;

/// <summary>
/// يجمع النصوص النهائية القادمة من التفريغ المباشر في جمل كاملة قبل كتابتها:
/// - إذا انتهى النص بعلامة نهاية جملة يُكتب فورًا.
/// - وإلا يُكتب بعد لحظة هدوء قصيرة (إن كانت الخدمة ترسل الكلام قطعًا صغيرة).
/// بهذا لا تُضاف نقطة في منتصف الجملة، ولا تُكتب كلمة بكلمة بطريقة تلخبط البرنامج.
/// </summary>
public sealed class LiveSegmenter
{
    private static readonly char[] SentenceEnds = ['.', '!', '?', '؟', '…', '۔'];
    private readonly StringBuilder _pending = new();
    private DateTime _lastAdd;

    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(650);

    public bool HasPending => _pending.Length > 0;

    /// <summary>يضيف نصًا نهائيًا ويعيد الجمل الجاهزة للكتابة (إن وُجدت).</summary>
    public IReadOnlyList<string> Add(string text, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (_pending.Length > 0 && !char.IsWhiteSpace(_pending[^1]) && !char.IsWhiteSpace(text[0]) && !char.IsPunctuation(text[0]))
        {
            _pending.Append(' ');
        }

        _pending.Append(text);
        _lastAdd = now;
        var trimmed = _pending.ToString().TrimEnd();
        return trimmed.Length > 0 && Array.IndexOf(SentenceEnds, trimmed[^1]) >= 0 ? [Flush()!] : [];
    }

    /// <summary>يُستدعى دوريًا: يكتب ما تجمّع إذا توقفت القطع لفترة.</summary>
    public string? Tick(DateTime now) => _pending.Length > 0 && now - _lastAdd >= Quiet ? Flush() : null;

    public string? Flush()
    {
        var text = _pending.ToString().Trim();
        _pending.Clear();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// الفاصل الذي يُكتب قبل جملة جديدة بعد جملة سابقة في نفس الإملاء:
    /// سطر جديد إذا كان كل جملة في سطر، وإلا مسافة.
    /// </summary>
    public static string Separator(bool sentencePerLine, string? previous, string next)
    {
        if (string.IsNullOrEmpty(previous) || next.StartsWith('\n') || previous.EndsWith('\n'))
        {
            return string.Empty;
        }

        return sentencePerLine ? "\n" : " ";
    }
}
