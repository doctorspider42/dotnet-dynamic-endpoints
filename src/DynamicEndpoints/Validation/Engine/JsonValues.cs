using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Validation.Engine;

/// <summary>Small helpers over <see cref="JsonNode"/> shared by the schema validator and the JsonLogic evaluator.</summary>
internal static class JsonValues
{
    public static JsonValueKind KindOf(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    public static string KindName(JsonNode? node) => KindOf(node) switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => IsInteger(node) ? "integer" : "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    public static bool TryGetNumber(JsonNode? node, out decimal value)
    {
        value = 0;
        if (node is not JsonValue json || json.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (json.TryGetValue(out value))
        {
            return true;
        }

        if (json.TryGetValue<long>(out var l))
        {
            value = l;
            return true;
        }

        if (json.TryGetValue<double>(out var d) && double.IsFinite(d))
        {
            value = d > (double)decimal.MaxValue ? decimal.MaxValue : d < (double)decimal.MinValue ? decimal.MinValue : (decimal)d;
            return true;
        }

        return decimal.TryParse(json.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static bool IsInteger(JsonNode? node) => TryGetNumber(node, out var d) && decimal.Truncate(d) == d;

    public static bool TryGetString(JsonNode? node, out string value)
    {
        value = string.Empty;
        return node is JsonValue json && json.GetValueKind() == JsonValueKind.String && json.TryGetValue(out value!);
    }

    /// <summary>JSON equality: numbers by value (1 == 1.0), objects regardless of property order.</summary>
    public static bool DeepEquals(JsonNode? a, JsonNode? b)
    {
        var kind = KindOf(a);
        if (kind != KindOf(b))
        {
            return false;
        }

        switch (kind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return true;
            case JsonValueKind.Number:
                return TryGetNumber(a, out var x) && TryGetNumber(b, out var y) && x == y;
            case JsonValueKind.String:
                return TryGetString(a, out var s) && TryGetString(b, out var t) && string.Equals(s, t, StringComparison.Ordinal);
            case JsonValueKind.Array:
                var left = a!.AsArray();
                var right = b!.AsArray();
                return left.Count == right.Count && left.Zip(right).All(p => DeepEquals(p.First, p.Second));
            case JsonValueKind.Object:
                var lo = a!.AsObject();
                var ro = b!.AsObject();
                return lo.Count == ro.Count && lo.All(p => ro.TryGetPropertyValue(p.Key, out var other) && DeepEquals(p.Value, other));
            default:
                return false;
        }
    }

    public static string FormatNumber(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);
}
