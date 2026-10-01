using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Validation.Engine;

/// <summary>
/// A deliberately small JSON Schema (2020-12) validator. It supports the keywords dynamic endpoints need and
/// <b>rejects every other keyword when a schema is saved</b>, so a schema is never silently half-enforced.
/// </summary>
internal static class JsonSchemaLite
{
    private static readonly HashSet<string> TypeNames = ["string", "integer", "number", "boolean", "object", "array", "null"];

    // Documentation-only keywords – accepted, ignored during validation.
    private static readonly HashSet<string> Annotations =
        ["$schema", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly"];

    private static readonly HashSet<string> Supported =
    [
        "type", "enum", "const", "format",
        "minLength", "maxLength",
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "properties", "required", "additionalProperties", "minProperties", "maxProperties",
        "items", "minItems", "maxItems", "uniqueItems",
    ];

    public static IReadOnlyList<string> SupportedKeywords => [.. Supported.Order()];

    /// <summary>Problems that make the schema unusable (unsupported keywords, malformed values).</summary>
    public static List<string> CheckSupported(JsonNode? schema)
    {
        var problems = new List<string>();
        CheckSchema(schema, "#", problems);
        return problems;
    }

    /// <summary>Reports each violation with the JSON path segments of the offending value.</summary>
    public static void Validate(JsonObject schema, JsonNode? instance, Action<IReadOnlyList<string>, ErrorMessage> report) =>
        Validate(schema, instance, [], report);

    private static void Validate(JsonNode schemaNode, JsonNode? instance, List<string> path, Action<IReadOnlyList<string>, ErrorMessage> report)
    {
        if (schemaNode is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.False)
        {
            report(path, ErrorMessage.Of("notAllowed", DynamicValidationCodes.NotAllowed));
            return;
        }

        if (schemaNode is not JsonObject schema)
        {
            return;
        }

        if (schema["type"] is { } type && !MatchesType(type, instance))
        {
            report(path, ErrorMessage.Of("type", DynamicValidationCodes.Type, DescribeType(type), JsonValues.KindName(instance)));
            return; // other keywords make no sense for a value of the wrong type
        }

        if (schema["const"] is { } constant && !JsonValues.DeepEquals(constant, instance))
        {
            report(path, ErrorMessage.Of("const", DynamicValidationCodes.Const, constant.ToJsonString()));
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(v => JsonValues.DeepEquals(v, instance)))
        {
            report(path, ErrorMessage.Of("enum", DynamicValidationCodes.Enum, string.Join(", ", allowed.Select(v => v?.ToJsonString() ?? "null"))));
        }

        switch (JsonValues.KindOf(instance))
        {
            case JsonValueKind.String:
                ValidateString(schema, instance!.GetValue<string>(), path, report);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, instance, path, report);
                break;
            case JsonValueKind.Object:
                ValidateObject(schema, instance!.AsObject(), path, report);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, instance!.AsArray(), path, report);
                break;
        }
    }

    private static void ValidateString(JsonObject schema, string value, List<string> path, Action<IReadOnlyList<string>, ErrorMessage> report)
    {
        var length = value.EnumerateRunes().Count(); // JSON Schema counts code points, not UTF-16 units
        if (Int(schema, "minLength") is { } min && length < min)
        {
            report(path, ErrorMessage.Counted("minLength", DynamicValidationCodes.MinLength, min, min));
        }

        if (Int(schema, "maxLength") is { } max && length > max)
        {
            report(path, ErrorMessage.Counted("maxLength", DynamicValidationCodes.MaxLength, max, max));
        }

        if (schema["format"] is JsonValue format && format.TryGetValue<string>(out var name) && StringFormats.Check(name, value) is { } error)
        {
            report(path, error);
        }
    }

    private static void ValidateNumber(JsonObject schema, JsonNode? instance, List<string> path, Action<IReadOnlyList<string>, ErrorMessage> report)
    {
        if (!JsonValues.TryGetNumber(instance, out var value))
        {
            return;
        }

        if (Number(schema, "minimum") is { } min && value < min)
        {
            report(path, ErrorMessage.Of("minimum", DynamicValidationCodes.Minimum, JsonValues.FormatNumber(min)));
        }

        if (Number(schema, "maximum") is { } max && value > max)
        {
            report(path, ErrorMessage.Of("maximum", DynamicValidationCodes.Maximum, JsonValues.FormatNumber(max)));
        }

        if (Number(schema, "exclusiveMinimum") is { } exMin && value <= exMin)
        {
            report(path, ErrorMessage.Of("exclusiveMinimum", DynamicValidationCodes.ExclusiveMinimum, JsonValues.FormatNumber(exMin)));
        }

        if (Number(schema, "exclusiveMaximum") is { } exMax && value >= exMax)
        {
            report(path, ErrorMessage.Of("exclusiveMaximum", DynamicValidationCodes.ExclusiveMaximum, JsonValues.FormatNumber(exMax)));
        }

        if (Number(schema, "multipleOf") is { } step && step > 0 && value % step != 0)
        {
            report(path, ErrorMessage.Of("multipleOf", DynamicValidationCodes.MultipleOf, JsonValues.FormatNumber(step)));
        }
    }

    private static void ValidateObject(JsonObject schema, JsonObject value, List<string> path, Action<IReadOnlyList<string>, ErrorMessage> report)
    {
        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r!.GetValue<string>()))
            {
                if (value[name] is null)
                {
                    report([.. path, name], ErrorMessage.Of("required", DynamicValidationCodes.Required));
                }
            }
        }

        var properties = schema["properties"] as JsonObject;
        foreach (var (name, child) in value)
        {
            var childPath = new List<string>(path) { name };
            if (properties?[name] is { } propertySchema)
            {
                Validate(propertySchema, child, childPath, report);
            }
            else if (schema["additionalProperties"] is { } additional)
            {
                if (additional is JsonValue v && v.GetValueKind() == JsonValueKind.False)
                {
                    report(childPath, ErrorMessage.Of("unknownField", DynamicValidationCodes.UnknownField));
                }
                else
                {
                    Validate(additional, child, childPath, report);
                }
            }
        }

        if (Int(schema, "minProperties") is { } min && value.Count < min)
        {
            report(path, ErrorMessage.Counted("minProperties", DynamicValidationCodes.MinProperties, min, min));
        }

        if (Int(schema, "maxProperties") is { } max && value.Count > max)
        {
            report(path, ErrorMessage.Counted("maxProperties", DynamicValidationCodes.MaxProperties, max, max));
        }
    }

    private static void ValidateArray(JsonObject schema, JsonArray value, List<string> path, Action<IReadOnlyList<string>, ErrorMessage> report)
    {
        if (Int(schema, "minItems") is { } min && value.Count < min)
        {
            report(path, ErrorMessage.Counted("minItems", DynamicValidationCodes.MinItems, min, min));
        }

        if (Int(schema, "maxItems") is { } max && value.Count > max)
        {
            report(path, ErrorMessage.Counted("maxItems", DynamicValidationCodes.MaxItems, max, max));
        }

        if (schema["uniqueItems"] is JsonValue unique && unique.GetValueKind() == JsonValueKind.True)
        {
            for (var i = 1; i < value.Count; i++)
            {
                if (Enumerable.Range(0, i).Any(j => JsonValues.DeepEquals(value[i], value[j])))
                {
                    report([.. path, i.ToString()], ErrorMessage.Of("uniqueItems", DynamicValidationCodes.UniqueItems));
                }
            }
        }

        if (schema["items"] is { } items)
        {
            for (var i = 0; i < value.Count; i++)
            {
                Validate(items, value[i], [.. path, i.ToString()], report);
            }
        }
    }

    private static bool MatchesType(JsonNode type, JsonNode? instance)
    {
        var names = type is JsonArray array ? array.Select(t => t!.GetValue<string>()) : [type.GetValue<string>()];
        var kind = JsonValues.KindName(instance);
        return names.Any(name => name == kind || (name == "number" && kind == "integer"));
    }

    private static string DescribeType(JsonNode type) =>
        type is JsonArray array ? string.Join(" or ", array.Select(t => t!.GetValue<string>())) : type.GetValue<string>();

    private static int? Int(JsonObject schema, string keyword) =>
        JsonValues.TryGetNumber(schema[keyword], out var d) ? (int)d : null;

    private static decimal? Number(JsonObject schema, string keyword) =>
        JsonValues.TryGetNumber(schema[keyword], out var d) ? d : null;

    // ------------------------------------------------------------------ schema checks

    private static void CheckSchema(JsonNode? node, string at, List<string> problems)
    {
        if (node is JsonValue boolean && boolean.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
        {
            return; // boolean schemas
        }

        if (node is not JsonObject schema)
        {
            problems.Add($"{at}: a schema must be a JSON object.");
            return;
        }

        foreach (var (keyword, value) in schema)
        {
            if (Annotations.Contains(keyword))
            {
                continue;
            }

            if (!Supported.Contains(keyword))
            {
                problems.Add($"{at}: unsupported keyword '{keyword}'. Supported: {string.Join(", ", SupportedKeywords)}.");
                continue;
            }

            var where = $"{at}/{keyword}";
            switch (keyword)
            {
                case "type":
                    var names = value is JsonArray types ? types.ToList() : [value];
                    if (names.Count == 0 || names.Any(n => !JsonValues.TryGetString(n, out var t) || !TypeNames.Contains(t)))
                    {
                        problems.Add($"{where}: must be one of {string.Join(", ", TypeNames)} (or an array of them).");
                    }

                    break;
                case "format":
                    if (!JsonValues.TryGetString(value, out var format) || !StringFormats.IsKnown(format))
                    {
                        problems.Add($"{where}: unknown format. Supported: {string.Join(", ", StringFormats.Names)}.");
                    }

                    break;
                case "enum":
                    if (value is not JsonArray { Count: > 0 })
                    {
                        problems.Add($"{where}: must be a non-empty array.");
                    }

                    break;
                case "minLength" or "maxLength" or "minItems" or "maxItems" or "minProperties" or "maxProperties":
                    if (!JsonValues.TryGetNumber(value, out var count) || count < 0 || decimal.Truncate(count) != count || count > int.MaxValue)
                    {
                        problems.Add($"{where}: must be a non-negative integer.");
                    }

                    break;
                case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf":
                    if (!JsonValues.TryGetNumber(value, out var number) || (keyword == "multipleOf" && number <= 0))
                    {
                        problems.Add($"{where}: must be a {(keyword == "multipleOf" ? "positive " : string.Empty)}number.");
                    }

                    break;
                case "uniqueItems":
                    if (JsonValues.KindOf(value) is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        problems.Add($"{where}: must be true or false.");
                    }

                    break;
                case "required":
                    if (value is not JsonArray required || required.Any(r => !JsonValues.TryGetString(r, out _)))
                    {
                        problems.Add($"{where}: must be an array of property names.");
                    }

                    break;
                case "properties":
                    if (value is not JsonObject properties)
                    {
                        problems.Add($"{where}: must be an object.");
                        break;
                    }

                    foreach (var (name, propertySchema) in properties)
                    {
                        CheckSchema(propertySchema, $"{where}/{name}", problems);
                    }

                    break;
                case "additionalProperties":
                    CheckSchema(value, where, problems);
                    break;
                case "items":
                    if (value is JsonArray)
                    {
                        problems.Add($"{where}: tuple validation (an array of schemas) is not supported.");
                    }
                    else
                    {
                        CheckSchema(value, where, problems);
                    }

                    break;
            }
        }
    }
}
