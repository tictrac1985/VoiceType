using VoiceType.Core.Speech;
using VoiceType.Providers.Templating;

namespace VoiceType.Providers.Definitions;

/// <summary>
/// يتحقق من ملف التعريف ويعيد أخطاء مفهومة، حتى يعرف المستخدم ما الذي يصلحه.
/// </summary>
public static class ProviderDefinitionValidator
{
    public static IReadOnlyList<string> Validate(ProviderDefinition definition)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(definition.Id))
        {
            errors.Add("الحقل id مطلوب.");
        }
        else if (definition.Id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            errors.Add("الحقل id يجب أن يحتوي على حروف إنجليزية وأرقام و - _ . فقط.");
        }

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add("الحقل name مطلوب.");
        }

        var request = definition.Request;
        if (string.IsNullOrWhiteSpace(request.Url))
        {
            errors.Add("الحقل request.url مطلوب.");
        }

        if (request.Method is not ("POST" or "PUT" or "post" or "put"))
        {
            errors.Add("الحقل request.method يجب أن يكون POST أو PUT.");
        }

        switch (request.Auth.Type)
        {
            case AuthDefinition.Bearer:
            case AuthDefinition.None:
                break;
            case AuthDefinition.Header:
            case AuthDefinition.QueryParameter:
                if (string.IsNullOrWhiteSpace(request.Auth.Name))
                {
                    errors.Add($"المصادقة من نوع {request.Auth.Type} تحتاج request.auth.name (اسم الـHeader أو المعامل).");
                }

                break;
            default:
                errors.Add("request.auth.type يجب أن يكون bearer أو header أو query أو none.");
                break;
        }

        switch (request.Body.Type)
        {
            case BodyDefinition.Multipart:
                if (string.IsNullOrWhiteSpace(request.Body.FileField))
                {
                    errors.Add("الطلب من نوع multipart يحتاج request.body.fileField.");
                }

                break;
            case BodyDefinition.Json:
                if (request.Body.Template is null)
                {
                    errors.Add("الطلب من نوع json يحتاج request.body.template.");
                }
                else if (!request.Body.Template.ToJsonString().Contains("{audio_base64", StringComparison.Ordinal))
                {
                    errors.Add("قالب json يجب أن يحتوي على {audio_base64} لإرسال الصوت.");
                }

                break;
            case BodyDefinition.Raw:
                break;
            default:
                errors.Add("request.body.type يجب أن يكون multipart أو json أو raw.");
                break;
        }

        if (string.IsNullOrWhiteSpace(definition.Response.TextPath))
        {
            errors.Add("الحقل response.textPath مطلوب (استخدم $ إذا كان الرد نصًا مباشرًا).");
        }

        foreach (var format in definition.Capabilities.Formats ?? [])
        {
            if (!AudioFormats.TryNormalize(format, out _))
            {
                errors.Add($"صيغة صوت غير معروفة في capabilities.formats: {format}");
            }
        }

        var unknown = FindUnknownVariables(definition);
        if (unknown.Count > 0)
        {
            errors.Add("متغيرات غير معروفة في القوالب: " + string.Join("، ", unknown.Select(v => "{" + v + "}")));
        }

        return errors;
    }

    private static List<string> FindUnknownVariables(ProviderDefinition definition)
    {
        var templates = new List<string> { definition.Request.Url };
        templates.AddRange(definition.Request.Headers.Values);
        templates.AddRange(definition.Request.Query.Values);
        templates.AddRange(definition.Request.Body.Fields.Values);
        if (definition.Request.Body.ContentType is { } contentType)
        {
            templates.Add(contentType);
        }

        if (definition.Request.Body.Template is { } template)
        {
            templates.Add(template.ToJsonString());
        }

        return templates
            .SelectMany(TemplateRenderer.GetVariableNames)
            .Where(name => !TemplateContext.BuiltInNames.Contains(name) && !definition.Variables.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
