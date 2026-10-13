using System.Net;
using VoiceType.Core.Speech;
using VoiceType.Evaluation.Comparison;
using VoiceType.Providers;
using VoiceType.Settings;
using VoiceType.Tests.Support;

namespace VoiceType.Tests.Lab;

public class LabTests
{
    [Theory]
    [InlineData("  sk-abc123  ", "sk-abc123")]
    [InlineData("\"sk-abc123\"", "sk-abc123")]
    [InlineData("Bearer sk-abc123", "sk-abc123")]
    [InlineData("sk-abc\n123\r\n", "sk-abc123")]
    [InlineData("   ", null)]
    public void Pasted_keys_are_cleaned(string input, string? expected) =>
        Assert.Equal(expected, SecureKeyStore.Clean(input));

    [Theory]
    [InlineData(SpeechErrorKind.Unauthorized, ConnectionStatus.Failed)]
    [InlineData(SpeechErrorKind.Network, ConnectionStatus.Failed)]
    [InlineData(SpeechErrorKind.PaymentRequired, ConnectionStatus.Warning)]
    [InlineData(SpeechErrorKind.RateLimited, ConnectionStatus.Warning)]
    [InlineData(SpeechErrorKind.BadRequest, ConnectionStatus.Ok)]
    [InlineData(SpeechErrorKind.InvalidResponse, ConnectionStatus.Ok)]
    public void Key_test_results_are_interpreted_for_humans(SpeechErrorKind kind, ConnectionStatus expected)
    {
        var result = ConnectionTester.Interpret(new SpeechException(kind, "x"));
        Assert.Equal(expected, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task Key_test_succeeds_against_working_provider()
    {
        var handler = FakeHttpHandler.Json("""{"text":""}""");
        var result = await ConnectionTester.TestAsync(ProviderCatalog.CreateDefault().Get("groq"), "k", handler.CreateClient());
        Assert.Equal(ConnectionStatus.Ok, result.Status);
    }

    [Fact]
    public async Task Key_test_reports_wrong_key()
    {
        var handler = FakeHttpHandler.Json("""{"error":{"message":"Invalid API Key"}}""", HttpStatusCode.Unauthorized);
        var result = await ConnectionTester.TestAsync(ProviderCatalog.CreateDefault().Get("openrouter"), "bad", handler.CreateClient());
        Assert.Equal(ConnectionStatus.Failed, result.Status);
    }

    [Fact]
    public void Lab_settings_round_trip_and_do_not_duplicate_models()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lab-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new LabSettings { Language = "ar" };
            Assert.True(settings.Add("openrouter", "openai/whisper-large-v3"));
            Assert.False(settings.Add("openrouter", "openai/whisper-large-v3"));
            settings.Save(path);

            var loaded = LabSettings.Load(path);
            Assert.Single(loaded.Models);
            Assert.Equal("ar", loaded.Language);
            Assert.True(loaded.Remove("openrouter", "openai/whisper-large-v3"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Comparison_writes_html_markdown_and_csv_reports()
    {
        var audioDir = Directory.CreateTempSubdirectory();
        var outDir = Path.Combine(audioDir.FullName, "out");
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(audioDir.FullName, "01.wav"), SampleAudio.Silence().Data);
            await File.WriteAllTextAsync(Path.Combine(audioDir.FullName, "01.txt"), "افتح ChatGPT");

            var handler = FakeHttpHandler.Json("""{"text":"افتح شات جي بي تي"}""");
            var runner = new ComparisonRunner(ProviderCatalog.CreateDefault(), handler.CreateClient());
            var samples = ComparisonRunner.LoadSamples(audioDir.FullName);
            var results = await runner.RunAsync(samples, [new CompareTarget("groq", "whisper-large-v3", "k"), new CompareTarget("cohere", null, null)], new TranscriptionOptions());

            ReportWriter.Write(outDir, results, "ar");

            var html = await File.ReadAllTextAsync(Path.Combine(outDir, "report.html"));
            Assert.Contains("dir=\"rtl\"", html);
            Assert.Contains("groq:whisper-large-v3", html);
            Assert.Contains("لا يوجد مفتاح", html);
            Assert.True(File.Exists(Path.Combine(outDir, "report.md")));
            Assert.True(File.Exists(Path.Combine(outDir, "results.csv")));

            var technical = Assert.Single(results, r => r.Text is not null).TechnicalScore!;
            Assert.Equal(1, technical.LatinTermsFound);
        }
        finally
        {
            audioDir.Delete(true);
        }
    }
}

public class FreeFirstTests
{
    [Fact]
    public void Every_built_in_provider_declares_its_pricing()
    {
        var catalog = ProviderCatalog.CreateDefault();
        Assert.All(catalog.All, d => Assert.Contains(d.Pricing, new[] { "free", "free-tier", "trial", "paid" }));
        Assert.Equal("free-tier", catalog.Get("gemini").Pricing);
        Assert.True(catalog.Get("openrouter").IsPaid);
    }

    [Fact]
    public async Task Gemini_transcribe_sends_inline_audio_with_egyptian_language_code()
    {
        var handler = FakeHttpHandler.Json("""
            { "id": "x", "status": "completed", "steps": [ { "type": "model_output", "content": [ { "type": "text", "text": "إزيك يا محمد" } ] } ] }
            """);
        var engine = new VoiceType.Providers.Engines.DeclarativeSpeechEngine(
            ProviderCatalog.CreateDefault().Get("gemini"),
            new VoiceType.Providers.Definitions.ProviderInstance { Definition = "gemini", ApiKey = "g-key" },
            handler.CreateClient());

        var result = await engine.TranscribeAsync(SampleAudio.Silence(), new TranscriptionOptions { Language = "ar" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/interactions", request.Uri.ToString());
        Assert.Equal("g-key", request.Headers["x-goog-api-key"]);
        var json = System.Text.Json.Nodes.JsonNode.Parse(request.BodyText)!;
        Assert.Equal("gemini-3.5-transcribe", (string?)json["model"]);
        Assert.Equal("audio/wav", (string?)json["input"]![0]!["mime_type"]);
        Assert.Equal("ar-EG", (string?)json["generation_config"]!["transcription_config"]!["language_codes"]![0]);
        Assert.Equal("إزيك يا محمد", result.Text);
    }

    [Theory]
    [InlineData("""{"output_text":"a"}""", "a")]
    [InlineData("""{"steps":[{"content":[{"text":"x"}]},{"content":[{"text":"last"}]}]}""", "last")]
    public void Path_alternatives_and_negative_indexes_work(string json, string expected) =>
        Assert.Equal(expected, VoiceType.Providers.Templating.JsonPathLite.SelectString(
            System.Text.Json.Nodes.JsonNode.Parse(json), "output_text|steps[-1].content[0].text"));
}
