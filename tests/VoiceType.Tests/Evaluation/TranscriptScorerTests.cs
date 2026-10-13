using VoiceType.Evaluation;

namespace VoiceType.Tests.Evaluation;

public class TranscriptScorerTests
{
    [Fact]
    public void Identical_text_has_zero_error()
    {
        var score = TranscriptScorer.Score("السلام عليكم", "السلام عليكم");
        Assert.Equal(0, score.Wer);
        Assert.Equal(0, score.Cer);
    }

    [Fact]
    public void Spelling_variants_diacritics_and_punctuation_are_normalized()
    {
        var score = TranscriptScorer.Score("أحمدُ ذهبَ إلى المدرسة.", "احمد ذهب الي المدرسه");
        Assert.Equal(0, score.Wer);
    }

    [Fact]
    public void Arabic_indic_digits_match_western_digits()
    {
        Assert.Equal(0, TranscriptScorer.Score("عندي ٣ ملفات", "عندي 3 ملفات").Wer);
    }

    [Fact]
    public void One_wrong_word_out_of_four_is_25_percent()
    {
        var score = TranscriptScorer.Score("هل ممكن تبعت الملف", "هل ممكن تبعث الملف");
        Assert.Equal(0.25, score.Wer, 3);
    }

    [Fact]
    public void Missing_and_extra_words_count_as_errors()
    {
        Assert.Equal(0.5, TranscriptScorer.Score("واحد اتنين", "واحد").Wer, 3);
        Assert.Equal(0.5, TranscriptScorer.Score("واحد اتنين", "واحد اتنين تلاتة").Wer, 3);
    }

    [Fact]
    public void Latin_terms_measure_code_switching_quality()
    {
        var score = TranscriptScorer.Score("افتح ChatGPT واكتب Python code", "افتح شات جي بي تي واكتب Python code");
        Assert.Equal(3, score.LatinTermsTotal);
        Assert.Equal(2, score.LatinTermsFound);
        Assert.Equal(2d / 3, score.LatinTermAccuracy!.Value, 3);
    }

    [Fact]
    public void No_latin_terms_means_no_latin_score()
    {
        Assert.Null(TranscriptScorer.Score("كلام عربي فقط", "كلام عربي فقط").LatinTermAccuracy);
    }
}
