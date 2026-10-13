using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using VoiceType.Core.Speech;
using VoiceType.Providers;
using VoiceType.Providers.Definitions;
using VoiceType.Providers.Engines;
using VoiceType.Providers.Templating;
using VoiceType.Tests.Support;

namespace VoiceType.Tests.Providers;

public class CustomProviderTests
{
    private const string QueryAuthProvider = """
        {
          // مزود خيالي: المفتاح في الـQuery، والصوت base64 داخل JSON متداخل، والنص في مصفوفة
          "id": "acme-stt",
          "name": "ACME Speech",
          "variables": { "base_url": "https://stt.acme.test/api", "region": "eu" },
          "defaultModel": "acme-ar-2",
          "request": {
            "url": "{base_url}/{region}/recognize",
            "auth": { "type": "query", "name": "key" },
            "query": { "lang": "{language}" },
            "body": {
              "type": "json",
              "template": {
                "config": { "model": "{model}", "hints": "{prompt}" },
                "audio": { "content": "{audio_base64}", "encoding": "{audio_format}" }
              }
            }
          },
          "response": { "textPath": "results[0].alternatives[0].text" },
          "capabilities": { "formats": [ "wav", "flac" ], "supportsPrompt": true }
        }
        """;

    [Fact]
    public async Task Custom_json_definition_works_end_to_end()
    {
        var definition = ProviderDefinitionSerializer.Deserialize(QueryAuthProvider);
        var handler = FakeHttpHandler.Json("""{"results":[{"alternatives":[{"text":"من مزود مخصص"}]}]}""");
        var engine = new DeclarativeSpeechEngine(
            definition,
            new ProviderInstance { Definition = "acme-stt", ApiKey = "k-123", Variables = { ["region"] = "me" } },
            handler.CreateClient());

        var result = await engine.TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions { Language = "ar", Prompt = "Python" });

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://stt.acme.test/api/me/recognize?lang=ar&key=k-123", request.Uri.ToString());
        var json = JsonNode.Parse(request.BodyText)!;
        Assert.Equal("acme-ar-2", (string?)json["config"]!["model"]);
        Assert.Equal("Python", (string?)json["config"]!["hints"]);
        Assert.Equal("wav", (string?)json["audio"]!["encoding"]);
        Assert.Equal("من مزود مخصص", result.Text);
    }

    [Fact]
    public void User_definition_overrides_built_in_with_same_id()
    {
        var catalog = ProviderCatalog.CreateDefault();
        var custom = ProviderDefinitionSerializer.Deserialize(QueryAuthProvider.Replace("\"acme-stt\"", "\"groq\"", StringComparison.Ordinal));
        catalog.Add(custom);

        Assert.Equal("ACME Speech", catalog.Get("groq").Name);
    }

    [Fact]
    public void Catalog_reports_invalid_custom_files_without_crashing()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory.FullName, "good.json"), QueryAuthProvider);
            File.WriteAllText(Path.Combine(directory.FullName, "bad.json"), """{ "id": "bad", "name": "Bad", "request": { "url": "" } }""");

            var catalog = ProviderCatalog.CreateDefault(directory.FullName);

            Assert.True(catalog.TryGet("acme-stt", out _));
            Assert.False(catalog.TryGet("bad", out _));
            Assert.Single(catalog.LoadErrors);
            Assert.Contains("request.url", catalog.LoadErrors[0]);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void Validator_explains_what_is_wrong()
    {
        var definition = new ProviderDefinition
        {
            Id = "x y",
            Name = "",
            Request = new RequestDefinition
            {
                Url = "{base_url}/stt/{unknown_thing}",
                Auth = new AuthDefinition { Type = "header" },
                Body = new BodyDefinition { Type = "json", Template = JsonNode.Parse("""{"model":"{model}"}""") },
            },
        };

        var errors = ProviderDefinitionValidator.Validate(definition);

        Assert.Contains(errors, e => e.Contains("id"));
        Assert.Contains(errors, e => e.Contains("name"));
        Assert.Contains(errors, e => e.Contains("auth.name"));
        Assert.Contains(errors, e => e.Contains("{audio_base64}"));
        Assert.Contains(errors, e => e.Contains("{unknown_thing}") && e.Contains("{base_url}"));
    }

    [Fact]
    public void Template_renderer_drops_empty_fields_and_types_numbers()
    {
        var template = JsonNode.Parse("""
            { "a": "{model}", "b": "{language}", "t": "{temperature:number}", "nested": { "x": "{prompt}" }, "keep": 5, "mixed": "lang-{language}" }
            """);
        var context = new TemplateContext().Set("model", "m1").Set("temperature", "0.5");

        var rendered = TemplateRenderer.RenderJson(template, context)!.AsObject();

        Assert.Equal("m1", (string?)rendered["a"]);
        Assert.False(rendered.ContainsKey("b"));
        Assert.Equal(0.5, (double)rendered["t"]!);
        Assert.False(rendered.ContainsKey("nested"));
        Assert.Equal(5, (int)rendered["keep"]!);
        Assert.Equal("lang-", (string?)rendered["mixed"]);
    }

    [Theory]
    [InlineData("text", "a")]
    [InlineData("$.text", "a")]
    [InlineData("deep.list[1].value", "second")]
    [InlineData("segments", "one two")]
    [InlineData("missing.path", null)]
    [InlineData("deep.list[9].value", null)]
    public void JsonPathLite_selects_values(string path, string? expected)
    {
        var root = JsonNode.Parse("""
            { "text": "a", "deep": { "list": [ { "value": "first" }, { "value": "second" } ] }, "segments": [ { "text": "one" }, { "text": "two" } ] }
            """);

        Assert.Equal(expected, JsonPathLite.SelectString(root, path));
    }

    [Fact]
    public async Task Private_huggingface_endpoint_without_model_works()
    {
        var definition = ProviderDefinitionSerializer.Deserialize("""
            {
              "id": "hf-endpoint",
              "name": "My HF Endpoint",
              "variables": { "base_url": "https://xxxx.endpoints.huggingface.cloud" },
              "request": { "url": "{base_url}", "auth": { "type": "bearer" }, "body": { "type": "raw" } },
              "response": { "textPath": "text", "errorPath": "error" }
            }
            """);
        var handler = FakeHttpHandler.Json("""{"text":"مصري"}""");
        var engine = new DeclarativeSpeechEngine(definition, new ProviderInstance { Definition = "hf-endpoint", ApiKey = "hf_x" }, handler.CreateClient());

        var result = await engine.TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions());

        Assert.Equal("https://xxxx.endpoints.huggingface.cloud/", Assert.Single(handler.Requests).Uri.ToString());
        Assert.Equal("مصري", result.Text);
        Assert.Equal("hf-endpoint", result.EngineId);
    }

    [Fact]
    public async Task Plain_text_response_is_supported_with_dollar_path()
    {
        var json = QueryAuthProvider.Replace("\"results[0].alternatives[0].text\"", "\"$\"", StringComparison.Ordinal);
        var handler = new FakeHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("نص مباشر", Encoding.UTF8, "text/plain"),
        }));
        var engine = new DeclarativeSpeechEngine(
            ProviderDefinitionSerializer.Deserialize(json),
            new ProviderInstance { Definition = "acme-stt", ApiKey = "k" },
            handler.CreateClient());

        var result = await engine.TranscribeAsync(TestAudio.Wav(), new TranscriptionOptions());

        Assert.Equal("نص مباشر", result.Text);
    }
}
