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
                    errors.Add(key, $"The value does not match the pattern '{regex}'.");
                    break;
                case JsonArray array:
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is JsonValue item && item.TryGetValue<string>(out var itemValue) && !IsMatch(regex, itemValue))
                        {
                            errors.Add($"{key}[{i}]", $"The value does not match the pattern '{regex}'.");
                        }
                    }

                    break;
            }
        }
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
                errors.Add(key, rule.Definition.Message);
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
