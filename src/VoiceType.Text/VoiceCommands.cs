using System.Text;

namespace VoiceType.Text;

public enum VoiceCommand
{
    None,

    /// <summary>«امسح آخر جملة» / «تراجع»: يلغي آخر ما كُتب.</summary>
    DeleteLastSentence,

    /// <summary>«امسح آخر كلمة».</summary>
    DeleteLastWord,

    /// <summary>«امسح الكل»: يلغي كل ما كُتب في الإملاء الحالي (أو السابق).</summary>
    DeleteAll,
}

/// <summary>
/// أوامر التعديل بالصوت. الأمر يُنفّذ فقط إذا قيل وحده (بين وقفتين)، حتى لا تُمسح جملة فيها كلمة «امسح» بالخطأ.
/// </summary>
public static class VoiceCommands
{
    private static readonly Dictionary<string, VoiceCommand> Phrases = Build(
        (VoiceCommand.DeleteLastSentence,
        [
            "امسح اخر جمله", "امسح الجمله الاخيره", "امسح اخر جمله دي", "امسح الجمله دي", "امسح اخر سطر",
            "امسح اللي فات", "امسح اللي قبل كده", "امسح اللي قلته", "امسح اخر حاجه", "شيل اخر جمله", "شيل الجمله الاخيره",
            "الغي اخر جمله", "الغي الجمله الاخيره", "تراجع", "تراجع عن اخر جمله", "احذف اخر جمله", "احذف الجمله الاخيره",
            "undo", "delete last sentence", "delete that", "scratch that",
        ]),
        (VoiceCommand.DeleteLastWord,
        [
            "امسح اخر كلمه", "امسح الكلمه الاخيره", "امسح الكلمه دي", "شيل اخر كلمه", "احذف اخر كلمه", "احذف الكلمه الاخيره",
            "delete last word",
        ]),
        (VoiceCommand.DeleteAll,
        [
            "امسح الكل", "امسح كل حاجه", "امسح كل اللي اتكتب", "امسح كل الكلام", "امسح كل اللي قلته", "احذف الكل", "احذف كل حاجه",
            "delete all", "delete everything",
        ]));

    public static VoiceCommand Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return VoiceCommand.None;
        }

        var normalized = Normalize(text);
        return normalized.Length is > 0 and < 40 && Phrases.TryGetValue(normalized, out var command) ? command : VoiceCommand.None;
    }

    /// <summary>توحيد الكتابة: بدون تشكيل أو ترقيم، والهمزات والتاء المربوطة والألف المقصورة موحّدة.</summary>
    public static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var raw in text)
        {
            var c = raw switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => char.ToLowerInvariant(raw),
            };

            if (c is >= 'ً' and <= 'ٟ' or 'ٰ' or 'ـ')
            {
                continue; // تشكيل وتطويل
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastSpace = false;
            }
            else if (!lastSpace && (char.IsWhiteSpace(c) || c is '-' or '_'))
            {
                builder.Append(' ');
                lastSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private static Dictionary<string, VoiceCommand> Build(params (VoiceCommand Command, string[] Phrases)[] groups)
    {
        var map = new Dictionary<string, VoiceCommand>(StringComparer.Ordinal);
        foreach (var (command, phrases) in groups)
        {
            foreach (var phrase in phrases)
            {
                map[Normalize(phrase)] = command;

                // «يا صدى امسح…» و«ممكن تمسح…» صيغ شائعة
                map[Normalize("يا صدى " + phrase)] = command;
            }
        }

        return map;
    }
}
