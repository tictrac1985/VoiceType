using VoiceType.Core.Speech;
using VoiceType.Providers.Resilience;
using VoiceType.Settings;
using Xunit;

namespace VoiceType.Tests.Text;

/// <summary>إصلاح v2.13: الكتابة المباشرة مغلقة افتراضيًا، والردّ الفارغ لا يُعطّل المحرك الأساسي.</summary>
public class StabilityFixTests
{
    [Fact]
    public void Live_typing_is_off_by_default_and_old_setting_is_ignored()
    {
        Assert.False(new AppSettings().LiveTyping);

        var path = Path.Combine(Path.GetTempPath(), "vt-settings-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"LiveTyping": true, "Sounds": false}""");
        try
        {
            var loaded = AppSettings.Load(path);
            Assert.False(loaded.LiveTyping);
            Assert.False(loaded.Sounds);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_answer_does_not_skip_the_main_engine()
    {
        var health = new EngineHealth();
        health.Failure("gemini:gemini-3.5-transcribe", SpeechErrorKind.InvalidResponse);
        Assert.False(health.IsSkipped("gemini:gemini-3.5-transcribe"));

        health.Failure("gemini:gemini-3.5-transcribe", SpeechErrorKind.ServerError);
        Assert.True(health.IsSkipped("gemini:gemini-3.5-transcribe"));
    }
}
