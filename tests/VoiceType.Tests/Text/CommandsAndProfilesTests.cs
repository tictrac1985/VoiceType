using System.Net;
using VoiceType.Providers.Rewrite;
using VoiceType.Settings;
using VoiceType.Speech;
using VoiceType.Tests.Support;
using VoiceType.Text;
using Xunit;

namespace VoiceType.Tests.Text;

/// <summary>أوامر التعديل بالصوت، الصياغة، الإعدادات حسب البرنامج، ودمج صوت الاجتماعات.</summary>
public class CommandsAndProfilesTests
{
    [Theory]
    [InlineData("امسح آخر جملة.", VoiceCommand.DeleteLastSentence)]
    [InlineData("  امسح اخر جمله  ", VoiceCommand.DeleteLastSentence)]
    [InlineData("تراجع", VoiceCommand.DeleteLastSentence)]
    [InlineData("يا صدى امسح الجملة الأخيرة!", VoiceCommand.DeleteLastSentence)]
    [InlineData("امسح آخر كلمة", VoiceCommand.DeleteLastWord)]
    [InlineData("امسح الكل.", VoiceCommand.DeleteAll)]
    [InlineData("Delete last word", VoiceCommand.DeleteLastWord)]
    [InlineData("امسح آخر جملة في الملف ده وابعته لأحمد", VoiceCommand.None)]
    [InlineData("النهارده هنراجع الـ API", VoiceCommand.None)]
    [InlineData("", VoiceCommand.None)]
    public void Recognizes_commands_only_when_said_alone(string text, VoiceCommand expected)
    {
        Assert.Equal(expected, VoiceCommands.Parse(text));
    }

    [Fact]
    public async Task Rewriter_sends_instructions_and_returns_clean_text()
    {
        var handler = FakeHttpHandler.Json("""{"candidates":[{"content":{"parts":[{"text":"thinking…","thought":true},{"text":"\"السلام عليكم، أرجو إرسال التقرير اليوم.\""}]}}]}""");
        var rewriter = new TextRewriter(handler.CreateClient(), ["key-1"]);

        var result = await rewriter.RewriteAsync("ابعتلي التقرير النهارده", WritingMode.Formal);

        Assert.Equal("السلام عليكم، أرجو إرسال التقرير اليوم.", result);
        var request = Assert.Single(handler.Requests);
        Assert.EndsWith("/models/gemini-flash-latest:generateContent", request.Uri.AbsolutePath);
        Assert.Equal("key-1", request.Headers["x-goog-api-key"]);
        var body = System.Text.Json.Nodes.JsonNode.Parse(request.BodyText)!;
        Assert.Contains("الفصحى", (string)body["systemInstruction"]!["parts"]![0]!["text"]!);
        Assert.Equal("ابعتلي التقرير النهارده", (string)body["contents"]![0]!["parts"]![0]!["text"]!);
    }

    [Fact]
    public async Task Rewriter_tries_next_key_after_limit_and_next_model_after_not_found()
    {
        var calls = 0;
        var handler = new FakeHttpHandler((request, _) =>
        {
            calls++;
            var status = calls switch { 1 => HttpStatusCode.TooManyRequests, 2 => HttpStatusCode.NotFound, _ => HttpStatusCode.OK };
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? """{"candidates":[{"content":{"parts":[{"text":"Send me the report today."}]}}]}""" : "{}"),
            });
        });
        var rewriter = new TextRewriter(handler.CreateClient(), ["a", "b"]);

        var result = await rewriter.RewriteAsync("ابعتلي التقرير النهارده", WritingMode.English);

        Assert.Equal("Send me the report today.", result);
        Assert.Equal("a", handler.Requests[0].Headers["x-goog-api-key"]);
        Assert.Equal("b", handler.Requests[1].Headers["x-goog-api-key"]);
        Assert.Contains("gemini-flash-lite-latest", handler.Requests[2].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Normal_mode_does_not_call_the_service()
    {
        var handler = FakeHttpHandler.Json("{}");
        var result = await new TextRewriter(handler.CreateClient(), ["k"]).RewriteAsync("كما هو", WritingMode.Normal);
        Assert.Equal("كما هو", result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Rewriter_fails_clearly_when_every_attempt_fails()
    {
        var handler = FakeHttpHandler.Json("{}", HttpStatusCode.TooManyRequests);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new TextRewriter(handler.CreateClient(), ["k"]).RewriteAsync("نص", WritingMode.Email));
        Assert.Contains("429", ex.Message);
    }

    [Theory]
    [InlineData("WhatsApp", null, "chat")]
    [InlineData("chrome", "WhatsApp - Google Chrome", "chat")]
    [InlineData("msedge", "Inbox (3) - Gmail - Microsoft Edge", "mail")]
    [InlineData("WINWORD", "تقرير.docx - Word", "docs")]
    [InlineData("Code", "app.js - Visual Studio Code", "code")]
    [InlineData("chrome", "YouTube - Google Chrome", null)]
    public void Finds_the_profile_for_the_current_app(string process, string? title, string? expectedId)
    {
        Assert.Equal(expectedId, AppProfile.Find(AppProfile.Defaults(), process, title)?.Id);
    }

    [Fact]
    public void Disabled_profiles_are_ignored()
    {
        var profiles = AppProfile.Defaults();
        profiles.First(p => p.Id == "chat").Enabled = false;
        Assert.Null(AppProfile.Find(profiles, "WhatsApp", null));
    }

    [Fact]
    public void Settings_without_profiles_get_the_defaults()
    {
        var settings = new AppSettings();
        Assert.Contains(settings.Profiles, p => p.Id == "chat");
        Assert.Equal("normal", settings.WritingMode);
    }

    [Fact]
    public void Meeting_mix_keeps_the_longer_track_and_never_clips()
    {
        var system = new[] { 0.5f, 0.9f, -0.9f };
        var mic = new[] { 0.25f, 0.5f, -0.5f, 0.1f };

        var mixed = MeetingRecorder.Mix(system, mic);

        Assert.Equal([0.75f, 1f, -1f, 0.1f], mixed);
        Assert.Same(system, MeetingRecorder.Mix(system, []));
    }
}
