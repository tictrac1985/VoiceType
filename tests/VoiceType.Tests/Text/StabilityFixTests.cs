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

/// <summary>v2.15: قراءة مجلد نماذج Vibe المشترك حتى لا يتكرر النموذج.</summary>
public class SharedModelsTests
{
    [Fact]
    public void Reads_vibe_models_folder_when_it_exists()
    {
        var root = Path.Combine(Path.GetTempPath(), "vt-vibe-" + Guid.NewGuid().ToString("N"));
        var models = Path.Combine(root, "VibeModels");
        Directory.CreateDirectory(models);
        var config = Path.Combine(root, "app_config.json");
        File.WriteAllText(config, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["models_folder"] = models, ["model.path"] = "x" }));
        try
        {
            Assert.Equal(Path.GetFullPath(models), AppPaths.DetectVibeModels(config));

            Directory.Delete(models);
            Assert.Null(AppPaths.DetectVibeModels(config)); // المجلد اتمسح

            File.WriteAllText(config, "{ broken");
            Assert.Null(AppPaths.DetectVibeModels(config));
            Assert.Null(AppPaths.DetectVibeModels(Path.Combine(root, "missing.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class ModelFilterTests
{
    [Theory]
    [InlineData("ggml-large-v3-q5_0.bin", true)]
    [InlineData("ggml-base.bin", true)]
    [InlineData("ggml-silero-v6.2.0.bin", false)]
    [InlineData("ggml-silero-vad.bin", false)]
    public void Ignores_voice_activity_models(string file, bool expected)
    {
        Assert.Equal(expected, VoiceType.Speech.LocalModels.IsTranscriptionModel(file));
    }
}
