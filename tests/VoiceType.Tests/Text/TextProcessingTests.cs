using VoiceType.Text;

namespace VoiceType.Tests.Text;

public class TextProcessingTests
{
    [Theory]
    [InlineData("السلام عليكم يا محمد, أتمنى تكون بخير", "السلام عليكم يا محمد، أتمنى تكون بخير.")]
    [InlineData("هل ممكن تبعتلي الملف النهارده", "هل ممكن تبعتلي الملف النهارده؟")]
    [InlineData("وهل خلصت الشغل", "وهل خلصت الشغل؟")]
    [InlineData("إزيك ؟", "إزيك؟")]
    [InlineData("تمام،شكرا", "تمام، شكرا")]
    [InlineData("المبلغ 1,000 جنيه والنسبة 3.5 في المية", "المبلغ 1,000 جنيه والنسبة 3.5 في المية.")]
    [InlineData("تمام كده", "تمام كده")]
    [InlineData("انا   هخلص   الشغل بكرة", "انا هخلص الشغل بكرة.")]
    [InlineData("Hello world, how are you?", "Hello world, how are you?")]
    public void Punctuation_rules_are_safe_and_arabic_aware(string input, string expected) =>
        Assert.Equal(expected, ArabicPunctuation.Normalize(input));

    [Fact]
    public void Urls_and_emails_are_never_touched()
    {
        var result = ArabicPunctuation.Normalize("ابعت على test@mail.com وافتح https://example.com/a?b=1,2 بسرعة");
        Assert.Contains("test@mail.com", result);
        Assert.Contains("https://example.com/a?b=1,2", result);
    }

    [Fact]
    public void English_question_inside_arabic_sentence_gets_arabic_mark()
    {
        Assert.Equal("انت بتستخدم Python؟", ArabicPunctuation.Normalize("انت بتستخدم Python?"));
    }

    [Fact]
    public void Technical_dictionary_fixes_transliterated_terms()
    {
        var processor = new TextProcessor();
        var result = processor.Process("افتح شات جي بي تي واكتب له كود بايثون عشان يعمل إيه بي آي", TextMode.Technical);
        Assert.Equal("افتح ChatGPT واكتب له كود Python عشان يعمل API", result);
    }

    [Fact]
    public void Dictionary_matches_whole_words_only()
    {
        var dictionary = new TermDictionary([new("جافا", "Java")]);
        Assert.Equal("Java و جافاوي", dictionary.Apply("جافا و جافاوي"));
    }

    [Fact]
    public void Ambiguous_egyptian_words_are_not_in_the_default_dictionary()
    {
        var processor = new TextProcessor();
        Assert.Equal("أنا جيت امبارح وكنت أكسل أشتغل", processor.Process("أنا جيت امبارح وكنت أكسل أشتغل", TextMode.Technical));
    }

    [Fact]
    public void General_mode_applies_personal_and_technical_terms()
    {
        var personal = new TermDictionary([new("فويس تايب", "VoiceType")]);
        var processor = new TextProcessor(personal);

        var result = processor.Process("برنامج فويس تايب مكتوب بلغة بايثون", TextMode.General);

        // المصطلحات الإنجليزية المعروفة تُكتب بحروفها حتى في الوضع العادي (مهم مع النموذج المحلي)
        Assert.Equal("برنامج VoiceType مكتوب بلغة Python.", result);
    }

    [Fact]
    public void Raw_mode_returns_text_as_is()
    {
        var processor = new TextProcessor();
        Assert.Equal("هل كود بايثون, تمام", processor.Process("  هل كود بايثون, تمام ", TextMode.Raw));
    }

    [Fact]
    public void Technical_mode_does_not_add_terminal_period()
    {
        var processor = new TextProcessor();
        Assert.Equal("اعمل function اسمها getUser", processor.Process("اعمل function اسمها getUser", TextMode.Technical));
    }
}
