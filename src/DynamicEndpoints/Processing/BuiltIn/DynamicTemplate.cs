using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace DynamicEndpoints.Processing.BuiltIn;

/// <summary>
/// Placeholder templates of the built-in processors. JSON and text templates use <c>{{path}}</c> (<c>{{name}}</c>,
/// <c>{{address.city}}</c>, <c>{{response.items[0].id}}</c>); a JSON string that is nothing but a placeholder keeps the value's
/// JSON type. URL templates use <c>{name}</c> like route templates, and every value is URL-encoded. Values are never evaluated.
/// </summary>
internal static partial class DynamicTemplate
{
    private const string PathPattern = @"[A-Za-z_][A-Za-z0-9_]*(?:\[[0-9]+\]|\.[A-Za-z_][A-Za-z0-9_]*)*";

    [GeneratedRegex(@"\{\{\s*(" + PathPattern + @")\s*\}\}")]
    private static partial Regex DoubleBraces();

    [GeneratedRegex(@"^\{\{\s*(" + PathPattern + @")\s*\}\}$")]
    private static partial Regex WholeValue();

    [GeneratedRegex(@"\{(" + PathPattern + @")\}")]
    private static partial Regex SingleBraces();

    [GeneratedRegex(@"\{config:([^{}]+)\}")]
    private static partial Regex ConfigurationReference();

    [GeneratedRegex(@"\[([0-9]+)\]")]
    private static partial Regex Index();

    /// <summary>Renders a JSON template: placeholders in string values (keys are left alone).</summary>
    public static JsonNode? RenderJson(JsonNode? template, JsonObject data) => template switch
    {
        null => null,
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, RenderJson(p.Value, data)))),
        JsonArray array => new JsonArray(array.Select(i => RenderJson(i, data)).ToArray()),
        JsonValue value when value.GetValueKind() == JsonValueKind.String => RenderString(value.GetValue<string>(), data),
        _ => template.DeepClone(),
    };

    public static string RenderText(string template, JsonObject data) =>
        DoubleBraces().Replace(template, m => Text(Resolve(data, m.Groups[1].Value)));

    /// <summary>Renders a URL template; every value is escaped, so it can't add path segments, query parameters or change the host.</summary>
    public static string RenderUrl(string template, JsonObject data) =>
        SingleBraces().Replace(template, m => Resolve(data, m.Groups[1].Value) switch
        {
            JsonArray items => string.Join(",", items.Select(i => Uri.EscapeDataString(Text(i)))),
            var value => Uri.EscapeDataString(Text(value)),
        });

    /// <summary>Resolves <c>{config:Section:Key}</c> references (e.g. secrets in header values) and <c>{name}</c> placeholders.</summary>
    // One pass, so neither a parameter value nor a configuration value is ever expanded again.
    public static string RenderHeader(string template, JsonObject data, IConfiguration? configuration) =>
        HeaderPlaceholder().Replace(template, m => m.Groups[1].Success
            ? configuration?[m.Groups[1].Value] ?? string.Empty
            : Text(Resolve(data, m.Groups[2].Value)));

    [GeneratedRegex(@"\{config:([^{}]+)\}|\{(" + PathPattern + @")\}")]
    private static partial Regex HeaderPlaceholder();

    /// <summary>Configuration keys referenced with <c>{config:…}</c>.</summary>
    public static IEnumerable<string> ConfigurationKeys(string template) =>
        ConfigurationReference().Matches(template).Select(m => m.Groups[1].Value);

    /// <summary>Checks a URL template: absolute http(s), and no placeholders in the scheme, host or port.</summary>
    public static string? CheckUrl(string? template, string key)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"'{key}' is required.";
        }

        var schemeEnd = template.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return $"'{key}' must be an absolute http or https URL.";
        }

        var authorityEnd = template.IndexOfAny(['/', '?', '#'], schemeEnd + 3);
        var authority = authorityEnd < 0 ? template[(schemeEnd + 3)..] : template[(schemeEnd + 3)..authorityEnd];
        if (template[..schemeEnd].Contains('{') || authority.Contains('{'))
        {
            return $"'{key}' can't have placeholders in the scheme, host or port.";
        }

        var sample = SingleBraces().Replace(template, "x");
        return Uri.TryCreate(sample, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? null
            : $"'{key}' must be an absolute http or https URL.";
    }

    public static JsonNode? Resolve(JsonNode? data, string path)
    {
        var current = data;
        foreach (var segment in path.Split('.'))
        {
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            current = current is JsonObject obj && obj.TryGetPropertyValue(name, out var child) ? child : null;
            if (bracket >= 0)
            {
                foreach (Match index in Index().Matches(segment[bracket..]))
                {
                    var i = int.Parse(index.Groups[1].Value);
                    current = current is JsonArray array && i < array.Count ? array[i] : null;
                }
            }

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static string Text(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => value.ToJsonString(),
    };

    private static JsonNode? RenderString(string text, JsonObject data)
    {
        if (WholeValue().Match(text) is { Success: true } whole)
        {
            return Resolve(data, whole.Groups[1].Value)?.DeepClone();
        }

        return JsonValue.Create(RenderText(text, data));
    }

    /// <summary>The data templates see: the request parameters plus extra values (e.g. an upstream response).</summary>
    public static JsonObject Data(JsonObject parameters, params (string Name, JsonNode? Value)[] extra)
    {
        var data = (JsonObject)parameters.DeepClone();
        foreach (var (name, value) in extra)
        {
            data[name] = value;
        }

        return data;
    }

    public static JsonNode? ParseBody(byte[] body, string? contentType)
    {
        if (body.Length == 0)
        {
            return null;
        }

        if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                return JsonNode.Parse(body);
            }
            catch (JsonException)
            {
            }
        }

        return JsonValue.Create(Encoding.UTF8.GetString(body));
    }
}
