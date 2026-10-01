using System.Text.Json.Nodes;

namespace DynamicEndpoints.Runtime;

/// <summary>Translates parameter definitions into JSON Schema (draft 2020-12 / OpenAPI 3.1).</summary>
internal static class ParameterSchemas
{
    /// <remarks>
    /// Documentation schemas carry descriptions, defaults, examples and patterns. Validation schemas skip patterns –
    /// those are checked with a non-backtracking regex engine instead of the schema evaluator.
    /// </remarks>
    public static JsonObject Build(ParameterDefinition parameter, bool forDocumentation)
    {
        JsonObject schema;
        switch (parameter.Type)
        {
            case ParameterType.Array:
                schema = CloneOrNew(parameter.Schema);
                schema["type"] = "array";
                if (!schema.ContainsKey("items"))
                {
                    var items = new JsonObject();
                    ApplyScalar(items, parameter.ItemType ?? ParameterType.String, parameter, forDocumentation);
                    schema["items"] = items;
                }

                if (parameter.MinItems is { } minItems)
                {
                    schema["minItems"] = minItems;
                }

                if (parameter.MaxItems is { } maxItems)
                {
                    schema["maxItems"] = maxItems;
                }

                break;

            case ParameterType.Object:
                schema = CloneOrNew(parameter.Schema);
                schema["type"] = "object";
                break;

            default:
                schema = new JsonObject();
                ApplyScalar(schema, parameter.Type, parameter, forDocumentation);
                break;
        }

        if (forDocumentation)
        {
            if (parameter.Description is { } description)
            {
                schema["description"] = description;
            }

            if (parameter.Default is { } defaultValue)
            {
                schema["default"] = defaultValue.DeepClone();
            }

            if (parameter.Example is { } example)
            {
                schema["examples"] = new JsonArray(example.DeepClone());
            }
        }

        return schema;
    }

    public static JsonObject BuildObject(IEnumerable<ParameterDefinition> parameters, bool forDocumentation, bool useSourceNames)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            var name = useSourceNames ? parameter.EffectiveSourceName : parameter.Name;
            properties[name] = Build(parameter, forDocumentation);
            if (parameter.Required)
            {
                required.Add(name);
            }
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };
        if (forDocumentation && required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static void ApplyScalar(JsonObject schema, ParameterType type, ParameterDefinition p, bool forDocumentation)
    {
        switch (type)
        {
            case ParameterType.String:
                schema["type"] = "string";
                if (p.MinLength is { } minLength)
                {
                    schema["minLength"] = minLength;
                }

                if (p.MaxLength is { } maxLength)
                {
                    schema["maxLength"] = maxLength;
                }

                if (p.Format is { } format)
                {
                    schema["format"] = Validation.Engine.StringFormats.ToSchemaName(format);
                }

                if (forDocumentation && p.Pattern is { } pattern)
                {
                    schema["pattern"] = pattern;
                }

                break;
            case ParameterType.Integer:
            case ParameterType.Number:
                schema["type"] = type == ParameterType.Integer ? "integer" : "number";
                if (p.Minimum is { } minimum)
                {
                    schema["minimum"] = minimum;
                }

                if (p.Maximum is { } maximum)
                {
                    schema["maximum"] = maximum;
                }

                break;
            case ParameterType.Boolean:
                schema["type"] = "boolean";
                break;
            case ParameterType.Date:
                schema["type"] = "string";
                schema["format"] = "date";
                break;
            case ParameterType.DateTime:
                schema["type"] = "string";
                schema["format"] = "date-time";
                break;
            case ParameterType.Guid:
                schema["type"] = "string";
                schema["format"] = "uuid";
                break;
            case ParameterType.Object:
                schema["type"] = "object";
                return;
            case ParameterType.Array:
                schema["type"] = "array";
                return;
            case ParameterType.File:
                // Bound as { fileName, contentType, length } – documented as what clients send: binary content.
                if (forDocumentation)
                {
                    schema["type"] = "string";
                    schema["format"] = "binary";
                    if (p.AllowedContentTypes is [var single])
                    {
                        schema["contentMediaType"] = single;
                    }
                }
                else
                {
                    schema["type"] = "object";
                }

                return;
        }

        if (p.AllowedValues is { Count: > 0 } allowed)
        {
            schema["enum"] = new JsonArray(allowed.Select(v => v?.DeepClone()).ToArray());
        }
    }

    private static JsonObject CloneOrNew(JsonObject? schema) => schema?.DeepClone() as JsonObject ?? new JsonObject();
}
