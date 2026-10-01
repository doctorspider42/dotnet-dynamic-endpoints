using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicEndpoints.Validation.Engine;

namespace DynamicEndpoints.Runtime;

internal static class RequestValidator
{
    /// <summary>Structural validation: JSON Schema (types, ranges, lengths, formats, enums) plus ReDoS-safe patterns.</summary>
    public static void ValidateStructure(CompiledEndpoint endpoint, JsonObject values, ValidationErrors errors)
    {
        SchemaEvaluation.Evaluate(endpoint.ValidationSchema, values, name => SourceName(endpoint, name), errors);

        foreach (var parameter in endpoint.Parameters)
        {
            if (parameter.Pattern is not { } regex || !values.TryGetPropertyValue(parameter.Definition.Name, out var value))
            {
                continue;
            }

            var key = parameter.Definition.EffectiveSourceName;
            switch (value)
            {
                case JsonValue v when v.TryGetValue<string>(out var s) && !IsMatch(regex, s):
                    errors.Add(key, ErrorMessage.Of("pattern", DynamicValidationCodes.Pattern, regex.ToString()));
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue item && item.TryGetValue<string>(out var itemValue) && !IsMatch(regex, itemValue))
                        {
                            errors.Add($"{key}[{i}]", ErrorMessage.Of("pattern", DynamicValidationCodes.Pattern, regex.ToString()));
                        }
                    }

                    break;
            }
        }

        foreach (var parameter in endpoint.Parameters)
        {
            var p = parameter.Definition;
            if ((p.MaxFileSize is not null || p.AllowedContentTypes is not null) && values[p.Name] is { } files)
            {
                ValidateFiles(p, files, errors);
            }
        }
    }

    private static void ValidateFiles(ParameterDefinition p, JsonNode files, ValidationErrors errors)
    {
        var key = p.EffectiveSourceName;
        var items = files is JsonArray array ? array.Select((f, i) => (f, $"{key}[{i}]")) : [(files, key)];
        foreach (var (file, itemKey) in items)
        {
            if (p.MaxFileSize is { } max && file?["length"]?.GetValue<long>() > max)
            {
                errors.Add(itemKey, ErrorMessage.Counted("file.maxSize", DynamicValidationCodes.FileSize, max, max));
            }

            var contentType = file?["contentType"]?.GetValue<string>() ?? string.Empty;
            if (p.AllowedContentTypes is { Count: > 0 } allowed && !allowed.Any(a => ContentTypeMatches(a, contentType)))
            {
                errors.Add(itemKey, ErrorMessage.Of("file.contentType", DynamicValidationCodes.FileType, contentType, string.Join(", ", allowed)));
            }
        }
    }

    /// <summary><c>image/*</c> matches <c>image/png</c>; parameters (<c>; charset=…</c>) are ignored.</summary>
    internal static bool ContentTypeMatches(string allowed, string actual)
    {
        static string Bare(string value) => value.Split(';', 2)[0].Trim();
        var pattern = Bare(allowed);
        var type = Bare(actual);
        return pattern == "*/*" ||
            (pattern.EndsWith("/*", StringComparison.Ordinal)
                ? type.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : string.Equals(pattern, type, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Business rules – only evaluated when the request is structurally valid.</summary>
    public static void ValidateRules(CompiledEndpoint endpoint, JsonObject values, ValidationErrors errors)
    {
        foreach (var rule in endpoint.Rules)
        {
            bool passed;
            try
            {
                passed = JsonLogic.IsTruthy(JsonLogic.Apply(rule.Condition, values));
            }
            catch (Exception)
            {
                passed = false;
            }

            if (!passed)
            {
                var key = rule.Definition.Parameter is { } name ? SourceName(endpoint, name) : "request";
                errors.Add(key, rule.Definition.Message, rule.Definition.Code ?? DynamicValidationCodes.Rule);
            }
        }
    }

    private static bool IsMatch(Regex regex, string value)
    {
        try
        {
            return regex.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string SourceName(CompiledEndpoint endpoint, string name) =>
        endpoint.ParametersByName.TryGetValue(name, out var p) ? p.Definition.EffectiveSourceName : name;
}
