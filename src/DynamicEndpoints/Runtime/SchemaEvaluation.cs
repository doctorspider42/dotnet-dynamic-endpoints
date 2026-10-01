using System.Text;
using System.Text.Json.Nodes;
using DynamicEndpoints.Validation.Engine;

namespace DynamicEndpoints.Runtime;

internal static class SchemaEvaluation
{
    /// <summary>Checks that the schema only uses supported keywords and returns a private copy of it.</summary>
    /// <exception cref="ArgumentException">The schema uses unsupported or malformed keywords.</exception>
    public static JsonObject Build(JsonObject schema)
    {
        var problems = JsonSchemaLite.CheckSupported(schema);
        if (problems.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", problems));
        }

        return (JsonObject)schema.DeepClone();
    }

    /// <summary>Evaluates <paramref name="instance"/> and reports errors keyed by a dotted path (<c>address.city</c>, <c>tags[1]</c>).</summary>
    public static void Evaluate(JsonObject schema, JsonNode instance, Func<string, string> mapRootProperty, ValidationErrors errors) =>
        JsonSchemaLite.Validate(schema, instance, (path, message) => errors.Add(ToKey(path, mapRootProperty), message));

    private static string ToKey(IReadOnlyList<string> segments, Func<string, string> mapRootProperty)
    {
        if (segments.Count == 0)
        {
            return "request";
        }

        var key = new StringBuilder(mapRootProperty(segments[0]));
        foreach (var segment in segments.Skip(1))
        {
            if (segment.Length > 0 && segment.All(char.IsAsciiDigit))
            {
                key.Append('[').Append(segment).Append(']');
            }
            else
            {
                key.Append('.').Append(segment);
            }
        }

        return key.ToString();
    }
}
