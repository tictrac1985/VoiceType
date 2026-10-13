namespace VoiceType.Text;

public static class ArabicText
{
    /// <summary>حرف عربي (يشمل الحروف والتشكيل، ولا يشمل الأرقام وعلامات الترقيم العربية).</summary>
    public static bool IsArabicLetter(char c) =>
        (c >= 'ء' && c <= 'ي') ||
        (c >= 'ً' && c <= 'ٟ') ||
        c == 'ٰ' ||
        (c >= 'ٱ' && c <= 'ۓ') ||
        (c >= 'ۺ' && c <= 'ۿ') ||
        (c >= 'ݐ' && c <= 'ݿ') ||
        (c >= 'ࢠ' && c <= 'ࣿ') ||
        (c >= 'ﭐ' && c <= '﷿') ||
        (c >= 'ﹰ' && c <= '﻿');

    public static bool IsLatinLetter(char c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');

    public static bool ContainsArabic(string text) => text.Any(IsArabicLetter);

    /// <summary>أقرب حرف غير مسافة قبل الموضع.</summary>
    internal static char? PreviousNonSpace(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return null;
    }

    internal static char? NextNonSpace(string text, int index)
    {
        for (var i = index + 1; i < text.Length; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return text[i];
            }
        }

        return null;
    }
}
