using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceType.Text;

/// <summary>تصحيح تعلّمه البرنامج: ما كتبه النموذج المحلي ← ما كتبه Gemini لنفس الكلام.</summary>
public sealed class LearnedRule
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;

    /// <summary>عدد مرات ظهور نفس التصحيح.</summary>
    public int Count { get; set; }

    public DateTime LastSeen { get; set; }
}

/// <summary>كلمة إنجليزية ظهرت في نصوص Gemini.</summary>
public sealed class LearnedTerm
{
    public string Word { get; set; } = string.Empty;

    public int Count { get; set; }

    public DateTime LastSeen { get; set; }
}

/// <summary>
/// التعلّم من Gemini محليًا: بعد كل جملة يكتبها Gemini بنجاح، يُشغَّل النموذج المحلي على نفس التسجيل في الخلفية،
/// ويُقارن النصان كلمة بكلمة. ما يكتبه المحلي بحروف عربية بينما كتبه Gemini بالإنجليزي يُحفظ كتصحيح
/// (مثل "ورك سبيس" ← "Workspace")، والكلمات الإنجليزية تُضاف لنص التوجيه الذي يقلّده النموذج المحلي.
/// كل شيء في ملف صغير (data\learned.json) ينتقل مع البرنامج.
/// </summary>
public sealed partial class LearnedCorrections
{
    /// <summary>التصحيح لا يُطبَّق إلا بعد أن يتكرر هذا العدد من المرات (حتى لا يتعلم من خطأ عابر).</summary>
    public const int MinCountToApply = 2;

    private const int MaxRules = 500;
    private const int MaxTerms = 300;
    private readonly object _gate = new();
    private readonly string? _path;
    private List<LearnedRule> _rules = [];
    private List<LearnedTerm> _terms = [];
    private TermDictionary? _dictionary;

    public LearnedCorrections(string? path = null)
    {
        _path = path;
        Load();
    }

    [GeneratedRegex(@"[ً-ْٰـ]")]
    private static partial Regex DiacriticsRegex();

    [GeneratedRegex(@"^[\p{P}\p{S}]+|[\p{P}\p{S}]+$")]
    private static partial Regex EdgePunctuationRegex();

    public IReadOnlyList<LearnedRule> Rules
    {
        get
        {
            lock (_gate)
            {
                return _rules.OrderByDescending(r => r.Count).ThenByDescending(r => r.LastSeen).ToList();
            }
        }
    }

    public IReadOnlyList<LearnedTerm> Terms
    {
        get
        {
            lock (_gate)
            {
                return _terms.OrderByDescending(t => t.Count).ThenByDescending(t => t.LastSeen).ToList();
            }
        }
    }

    /// <summary>عدد التصحيحات المؤكدة (التي تُطبَّق فعلًا).</summary>
    public int ActiveRuleCount
    {
        get
        {
            lock (_gate)
            {
                return _rules.Count(r => r.Count >= MinCountToApply);
            }
        }
    }

    /// <summary>يقارن نص النموذج المحلي بنص Gemini لنفس التسجيل ويتعلم الفروق. يرجع عدد ما تعلّمه.</summary>
    public int Learn(string localText, string referenceText)
    {
        if (string.IsNullOrWhiteSpace(localText) || string.IsNullOrWhiteSpace(referenceText))
        {
            return 0;
        }

        var local = Tokenize(localText);
        var reference = Tokenize(referenceText);
        if (local.Count == 0 || reference.Count == 0 || local.Count > 400 || reference.Count > 400)
        {
            return 0;
        }

        var learned = 0;
        lock (_gate)
        {
            // الكلمات الإنجليزية في نص Gemini
            foreach (var word in reference.Where(t => IsLatinToken(t.Clean)).Select(t => t.Clean).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var term = _terms.FirstOrDefault(t => string.Equals(t.Word, word, StringComparison.OrdinalIgnoreCase));
                if (term is null)
                {
                    _terms.Add(term = new LearnedTerm { Word = word });
                    learned++;
                }

                term.Count++;
                term.LastSeen = DateTime.Now;
            }

            // المقارنة كلمة بكلمة، والتعلم فقط إذا كان النصان متشابهين في أغلبهما
            var blocks = Diff(local, reference, out var matched);
            if (matched * 2 >= Math.Min(local.Count, reference.Count))
            {
                foreach (var (localBlock, referenceBlock) in blocks)
                {
                    if (!IsUsefulCorrection(localBlock, referenceBlock))
                    {
                        continue;
                    }

                    var from = string.Join(' ', localBlock.Select(t => t.Clean));
                    var to = string.Join(' ', referenceBlock.Select(t => t.Clean));
                    var rule = _rules.FirstOrDefault(r => Normalize(r.From) == Normalize(from) && string.Equals(r.To, to, StringComparison.Ordinal));
                    if (rule is null)
                    {
                        _rules.Add(rule = new LearnedRule { From = from, To = to });
                        learned++;
                    }

                    rule.Count++;
                    rule.LastSeen = DateTime.Now;
                }
            }

            Trim();
            _dictionary = null;
            Save();
        }

        return learned;
    }

    /// <summary>يطبق التصحيحات المؤكدة على نص النموذج المحلي.</summary>
    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        TermDictionary dictionary;
        lock (_gate)
        {
            dictionary = _dictionary ??= new TermDictionary(_rules
                .Where(r => r.Count >= MinCountToApply)
                .GroupBy(r => Normalize(r.From))
                .Select(g => g.OrderByDescending(r => r.Count).First())
                .Select(r => new KeyValuePair<string, string>(r.From, r.To)));
        }

        return dictionary.Apply(text);
    }

    /// <summary>أكثر الكلمات الإنجليزية استخدامًا، لنص التوجيه الذي يقلّده النموذج المحلي.</summary>
    public IReadOnlyList<string> TopTerms(int count) =>
        Terms.Where(t => t.Count >= 1).Take(count).Select(t => t.Word).ToList();

    public void RemoveRule(string from, string to)
    {
        lock (_gate)
        {
            _rules.RemoveAll(r => r.From == from && r.To == to);
            _dictionary = null;
            Save();
        }
    }

    public void RemoveTerm(string word)
    {
        lock (_gate)
        {
            _terms.RemoveAll(t => string.Equals(t.Word, word, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _rules.Clear();
            _terms.Clear();
            _dictionary = null;
            Save();
        }
    }

    // ===== المقارنة =====
    private sealed record Token(string Clean, string Key);

    private static List<Token> Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => EdgePunctuationRegex().Replace(word, string.Empty))
            .Where(word => word.Length > 0)
            .Select(word => new Token(word, Normalize(word)))
            .ToList();

    /// <summary>توحيد الكتابة للمقارنة: التشكيل، أشكال الألف والياء والتاء المربوطة، وحالة الحروف الإنجليزية.</summary>
    public static string Normalize(string text)
    {
        var value = DiacriticsRegex().Replace(text, string.Empty).ToLowerInvariant();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ى' => 'ي',
                'ة' => 'ه',
                _ => c,
            });
        }

        return builder.ToString();
    }

    private static bool IsLatinToken(string token) =>
        token.Any(ArabicText.IsLatinLetter) && !token.Any(ArabicText.IsArabicLetter) && token.Length >= 2;

    private static bool IsArabicToken(string token) =>
        token.Any(ArabicText.IsArabicLetter) && !token.Any(ArabicText.IsLatinLetter);

    private static bool IsArticle(string token) => token is "الـ" or "ال";

    /// <summary>
    /// تصحيح مفيد = كلمات عربية في نص المحلي (1 إلى 4 كلمات) قابلها في نص Gemini كلمة إنجليزية أو أكثر
    /// (مع "الـ" اختياريًا). لا نتعلم تصحيح الكلام العربي نفسه، لأن Gemini قد يعيد الصياغة.
    /// </summary>
    private static bool IsUsefulCorrection(List<Token> localBlock, List<Token> referenceBlock)
    {
        if (localBlock.Count is 0 or > 4 || referenceBlock.Count is 0 or > 3)
        {
            return false;
        }

        if (!localBlock.All(t => IsArabicToken(t.Clean)))
        {
            return false;
        }

        if (!referenceBlock.All(t => IsLatinToken(t.Clean) || IsArticle(t.Clean)) || !referenceBlock.Any(t => IsLatinToken(t.Clean)))
        {
            return false;
        }

        var fromLength = localBlock.Sum(t => t.Clean.Length);
        return fromLength >= 3 && fromLength <= 40;
    }

    /// <summary>مقارنة بأطول تسلسل مشترك (LCS)، وترجع الأجزاء المختلفة متقابلة.</summary>
    private static List<(List<Token> Local, List<Token> Reference)> Diff(List<Token> a, List<Token> b, out int matched)
    {
        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i].Key == b[j].Key ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        matched = lcs[0, 0];
        var blocks = new List<(List<Token>, List<Token>)>();
        var localBlock = new List<Token>();
        var referenceBlock = new List<Token>();
        int x = 0, y = 0;

        void Flush()
        {
            if (localBlock.Count > 0 || referenceBlock.Count > 0)
            {
                blocks.Add((localBlock.ToList(), referenceBlock.ToList()));
                localBlock.Clear();
                referenceBlock.Clear();
            }
        }

        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Key == b[y].Key)
            {
                Flush();
                x++;
                y++;
            }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                referenceBlock.Add(b[y++]);
            }
            else
            {
                localBlock.Add(a[x++]);
            }
        }

        Flush();
        return blocks;
    }

    // ===== الحفظ =====
    private sealed class LearnedFile
    {
        public int Version { get; set; } = 1;

        public List<LearnedRule> Rules { get; set; } = [];

        public List<LearnedTerm> Terms { get; set; } = [];
    }

    private void Trim()
    {
        if (_rules.Count > MaxRules)
        {
            _rules = _rules.OrderByDescending(r => r.Count).ThenByDescending(r => r.LastSeen).Take(MaxRules).ToList();
        }

        if (_terms.Count > MaxTerms)
        {
            _terms = _terms.OrderByDescending(t => t.Count).ThenByDescending(t => t.LastSeen).Take(MaxTerms).ToList();
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            var file = JsonSerializer.Deserialize<LearnedFile>(File.ReadAllText(_path));
            _rules = file?.Rules ?? [];
            _terms = file?.Terms ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _rules = [];
            _terms = [];
        }
    }

    private void Save()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(new LearnedFile { Rules = _rules, Terms = _terms }, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, Encoding.UTF8);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
