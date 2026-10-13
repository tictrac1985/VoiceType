using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace VoiceType.Providers.Definitions;

public static class ProviderDefinitionSerializer
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    public static ProviderDefinition Deserialize(string json)
    {
        ProviderDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<ProviderDefinition>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProviderDefinitionException($"ملف التعريف ليس JSON صحيحًا: {ex.Message}", ex);
        }

        if (definition is null)
        {
            throw new ProviderDefinitionException("ملف التعريف فارغ.");
        }

        Normalize(definition);
        var errors = ProviderDefinitionValidator.Validate(definition);
        if (errors.Count > 0)
        {
            throw new ProviderDefinitionException(
                $"تعريف المزود '{definition.Id}' غير صالح:{Environment.NewLine}- " +
                string.Join(Environment.NewLine + "- ", errors));
        }

        return definition;
    }

    public static string Serialize(ProviderDefinition definition) =>
        JsonSerializer.Serialize(definition, Options);

    private static void Normalize(ProviderDefinition definition)
    {
        definition.Id = definition.Id.Trim();
        definition.Variables = new Dictionary<string, string>(definition.Variables ?? [], StringComparer.OrdinalIgnoreCase);
        definition.Models ??= [];
        definition.Request ??= new RequestDefinition();
        definition.Request.Auth ??= new AuthDefinition();
        definition.Request.Headers = new Dictionary<string, string>(definition.Request.Headers ?? [], StringComparer.OrdinalIgnoreCase);
        definition.Request.Query ??= [];
        definition.Request.Body ??= new BodyDefinition();
        definition.Request.Body.Fields ??= [];
        definition.Request.Auth.Type = definition.Request.Auth.Type.Trim().ToLowerInvariant();
        definition.Request.Body.Type = definition.Request.Body.Type.Trim().ToLowerInvariant();
        definition.Response ??= new ResponseDefinition();
        definition.Capabilities ??= new CapabilitiesDefinition();
    }
}

public sealed class ProviderDefinitionException(string message, Exception? inner = null)
    : Exception(message, inner);
