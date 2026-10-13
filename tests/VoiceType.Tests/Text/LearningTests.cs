using VoiceType.Text;
using Xunit;

namespace VoiceType.Tests.Text;

public class LearningTests
{
    [Fact]
    public void Learns_english_word_written_in_arabic_and_applies_after_two_times()
    {
        var learned = new LearnedCorrections();
        const string local = "النهارده هنفتح الورك سبيس ونشوف الباج";
        const string gemini = "النهارده هنفتح الـ Workspace ونشوف الـ bug.";

        learned.Learn(local, gemini);
        Assert.Equal(local, learned.Apply(local)); // مرة واحدة لا تكفي

        learned.Learn(local, gemini);
        Assert.Equal("النهارده هنفتح الـ Workspace ونشوف الـ bug", learned.Apply(local));
        Assert.Contains("Workspace", learned.TopTerms(10));
    }

    [Fact]
    public void Does_not_learn_arabic_rewording()
    {
        var learned = new LearnedCorrections();
        for (var i = 0; i < 3; i++)
        {
            learned.Learn("انا رايح الشغل بكره الصبح", "أنا رايح الشغل بكرة الصبح بدري");
        }

        Assert.Empty(learned.Rules);
    }

    [Fact]
    public void Ignores_very_different_transcripts()
    {
        var learned = new LearnedCorrections();
        learned.Learn("كلام مختلف تماما هنا", "Something totally different about Python and Docker here");
        Assert.Empty(learned.Rules);
    }

    [Fact]
    public void Saves_and_loads_from_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vt-learned-{Guid.NewGuid():N}.json");
        try
        {
            var first = new LearnedCorrections(path);
            first.Learn("افتح الجيت هب دلوقتي", "افتح الـ GitHub دلوقتي");
            first.Learn("افتح الجيت هب دلوقتي", "افتح الـ GitHub دلوقتي");

            var second = new LearnedCorrections(path);
            Assert.Equal("افتح الـ GitHub دلوقتي", second.Apply("افتح الجيت هب دلوقتي"));
            Assert.Equal(1, second.ActiveRuleCount);

            second.RemoveRule(second.Rules[0].From, second.Rules[0].To);
            Assert.Equal(0, new LearnedCorrections(path).ActiveRuleCount);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
