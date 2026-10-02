using System.Globalization;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Snippets;

/// <summary>
/// Generates plausible example values from parameter definitions and JSON Schemas: explicit examples first, then defaults,
/// allowed values and finally values derived from types, formats and limits.
/// </summary>
internal static class ExampleValues
{
    private const int MaxDepth = 8;

    public static JsonNode? For(ParameterDefinition p)
    {
        if (p.Example is not null)
        {
            return p.Example.DeepClone();
        }

        if (p.Default is not null)
        {
            return p.Default.DeepClone();
        }

        if (p.AllowedValues is [var first, ..])
        {
            return first?.DeepClone();
        }

        switch (p.Type)
        {
            case ParameterType.Object:
                return p.Schema is not null ? FromSchema(p.Schema, 0) : new JsonObject();
            case ParameterType.Array:
                if (p.Schema is not null)
                {
                    return FromSchema(p.Schema.DeepClone() is JsonObject s ? WithItemCounts(s, p) : p.Schema, 0);
                }

                var count = ItemCount(p.MinItems, p.MaxItems);
                var array = new JsonArray();
                for (var i = 0; i < count; i++)
                {
                    array.Add(Scalar(p.ItemType ?? ParameterType.String, p));
                }

                return array;
            default:
                return Scalar(p.Type, p);
        }
    }

    /// <summary>File name and content type of an example upload, derived from the allowed content types.</summary>
    public static (string FileName, string ContentType) File(ParameterDefinition p)
    {
        var contentType = p.AllowedContentTypes?.FirstOrDefault(c => !c.EndsWith("/*", StringComparison.Ordinal))
            ?? p.AllowedContentTypes?.FirstOrDefault() switch
            {
                "image/*" => "image/png",
                "text/*" => "text/plain",
                "audio/*" => "audio/mpeg",
                "video/*" => "video/mp4",
                _ => "application/octet-stream",
            };

        var extension = contentType switch
        {
            "application/pdf" => "pdf",
            "application/json" => "json",
            "application/zip" => "zip",
            "image/png" => "png",
            "image/jpeg" => "jpg",
            "image/gif" => "gif",
            "image/svg+xml" => "svg",
            "text/plain" => "txt",
            "text/csv" => "csv",
            "audio/mpeg" => "mp3",
            "video/mp4" => "mp4",
            _ => "bin",
        };
        return ($"{p.EffectiveSourceName}.{extension}", contentType);
    }

    public static JsonNode? FromSchema(JsonNode? schema, int depth)
    {
        if (schema is not JsonObject s || depth > MaxDepth)
        {
            return null;
        }

        if (s["examples"] is JsonArray { Count: > 0 } examples)
        {
            return examples[0]?.DeepClone();
        }

        foreach (var keyword in (string[])["example", "default", "const"])
        {
            if (s.TryGetPropertyValue(keyword, out var value))
            {
                return value?.DeepClone();
            }
        }

        if (s["enum"] is JsonArray { Count: > 0 } allowed)
        {
            return allowed[0]?.DeepClone();
        }

        // Compositions (OpenAPI response schemas): the parts of allOf merged, the first alternative of oneOf/anyOf.
        if (s["allOf"] is JsonArray { Count: > 0 } parts)
        {
            var merged = s.ContainsKey("properties") ? FromSchema(new JsonObject { ["type"] = "object", ["properties"] = s["properties"]!.DeepClone() }, depth) as JsonObject : null;
            merged ??= [];
            foreach (var part in parts)
            {
                switch (FromSchema(part, depth + 1))
                {
                    case JsonObject obj:
                        foreach (var (name, value) in obj)
                        {
                            merged[name] = value?.DeepClone();
                        }

                        break;
                    case var other when parts.Count == 1:
                        return other;
                }
            }

            return merged;
        }

        if ((s["oneOf"] ?? s["anyOf"]) is JsonArray { Count: > 0 } alternatives)
        {
            return FromSchema(alternatives[0], depth + 1);
        }

        switch (TypeOf(s))
        {
            case "object":
                var obj = new JsonObject();
                if (s["properties"] is JsonObject properties)
                {
                    foreach (var (name, property) in properties)
                    {
                        obj[name] = FromSchema(property, depth + 1);
                    }
                }

                return obj;
            case "array":
                var array = new JsonArray();
                var count = ItemCount(Int(s["minItems"]), Int(s["maxItems"]));
                for (var i = 0; i < count; i++)
                {
                    array.Add(FromSchema(s["items"] ?? new JsonObject { ["type"] = "string" }, depth + 1));
                }

                return array;
            case "integer":
                return Number(Dec(s["minimum"]), Dec(s["maximum"]), Dec(s["exclusiveMinimum"]), Dec(s["exclusiveMaximum"]), integer: true);
            case "number":
                return Number(Dec(s["minimum"]), Dec(s["maximum"]), Dec(s["exclusiveMinimum"]), Dec(s["exclusiveMaximum"]), integer: false);
            case "boolean":
                return true;
            case "null":
                return null;
            default:
                return Text(s["format"]?.GetValue<string>(), Int(s["minLength"]), Int(s["maxLength"]));
        }
    }

    private static JsonNode? Scalar(ParameterType type, ParameterDefinition p) => type switch
    {
        ParameterType.Integer => Number(p.Minimum, p.Maximum, null, null, integer: true),
        ParameterType.Number => Number(p.Minimum, p.Maximum, null, null, integer: false),
        ParameterType.Boolean => true,
        ParameterType.Date => "2026-01-31",
        ParameterType.DateTime => "2026-01-31T12:00:00Z",
        ParameterType.Guid => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
        ParameterType.Object => new JsonObject(),
        ParameterType.File => File(p).FileName,
        _ => Text(p.Format is { } format ? Validation.Engine.StringFormats.ToSchemaName(format) : null, p.MinLength, p.MaxLength),
    };

    private static JsonNode Text(string? format, int? minLength, int? maxLength)
    {
        var text = format switch
        {
            "email" => "user@example.com",
            "uri" => "https://example.com",
            "phone" => "+48123456789",
            "ipv4" => "192.0.2.1",
            "ipv6" => "2001:db8::1",
            "time" => "12:00",
            "date" => "2026-01-31",
            "date-time" => "2026-01-31T12:00:00Z",
            "uuid" => "3fa85f64-5717-4562-b3fc-2c963f66afa6",
            _ => "string",
        };

        if (format is null)
        {
            if (minLength is { } min && text.Length < min)
            {
                text = text.PadRight(Math.Min(min, 1024), 'x');
            }

            if (maxLength is { } max && text.Length > max)
            {
                text = text[..Math.Max(max, 0)];
            }
        }

        return JsonValue.Create(text)!;
    }

    private static JsonNode Number(decimal? minimum, decimal? maximum, decimal? exclusiveMinimum, decimal? exclusiveMaximum, bool integer)
    {
        var value = 1m;
        if (exclusiveMinimum is { } exMin && value <= exMin)
        {
            value = integer ? Math.Floor(exMin) + 1 : exMin + 1;
        }

        if (minimum is { } min && value < min)
        {
            value = integer ? Math.Ceiling(min) : min;
        }

        if (exclusiveMaximum is { } exMax && value >= exMax)
        {
            value = integer ? Math.Ceiling(exMax) - 1 : exMax - 1;
        }

        if (maximum is { } max && value > max)
        {
            value = integer ? Math.Floor(max) : max;
        }

        return integer ? JsonValue.Create((long)value) : JsonValue.Create(value);
    }

    private static int ItemCount(int? minItems, int? maxItems) =>
        Math.Min(Math.Max(minItems ?? 1, 0), maxItems ?? int.MaxValue) is var count && count > 16 ? 16 : count;

    private static JsonObject WithItemCounts(JsonObject schema, ParameterDefinition p)
    {
        if (p.MinItems is { } min)
        {
            schema["minItems"] = min;
        }

        if (p.MaxItems is { } max)
        {
            schema["maxItems"] = max;
        }

        schema["type"] = "array";
        return schema;
    }

    private static string? TypeOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue value when value.TryGetValue<string>(out var type) => type,
        JsonArray types => types.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t is not null and not "null"),
        _ when schema.ContainsKey("properties") => "object",
        _ when schema.ContainsKey("items") => "array",
        _ => null,
    };

    private static int? Int(JsonNode? node) => Dec(node) is { } number ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;

    public static decimal? Dec(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.Number &&
        decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    /// <summary>Text form of a value as sent in a route, query string, header or form field.</summary>
    public static string ToText(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => value.ToJsonString(),
    };
}
