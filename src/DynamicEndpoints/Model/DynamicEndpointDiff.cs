using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicEndpoints.Runtime;

namespace DynamicEndpoints;

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointDifferenceKind>))]
public enum DynamicEndpointDifferenceKind
{
    Added,
    Removed,
    Changed,
}

/// <summary>One difference between two definitions.</summary>
/// <param name="Path">
/// Where it is, e.g. <c>route</c>, <c>parameters[quantity].maximum</c> or <c>rules[0].message</c>. Items of lists whose entries
/// have unique names (parameters, validators) are addressed by name, everything else by index.
/// </param>
/// <param name="Kind">Whether the value was added, removed or changed.</param>
/// <param name="From">The old value; <c>null</c> for <see cref="DynamicEndpointDifferenceKind.Added"/>.</param>
/// <param name="To">The new value; <c>null</c> for <see cref="DynamicEndpointDifferenceKind.Removed"/>.</param>
public sealed record DynamicEndpointDifference(string Path, DynamicEndpointDifferenceKind Kind, JsonNode? From, JsonNode? To);

/// <summary>Structural comparison of definitions – what a publish or a rollback changes.</summary>
public static class DynamicEndpointDiff
{
    // Managed by the library – they differ between any two revisions and say nothing about the content.
    private static readonly string[] Ignored = ["revision", "createdAt", "updatedAt"];

    /// <summary>
    /// Differences from <paramref name="from"/> to <paramref name="to"/> (both normalized first). Pass <c>null</c> as
    /// <paramref name="from"/> for a definition that doesn't exist yet: everything is <see cref="DynamicEndpointDifferenceKind.Added"/>.
    /// </summary>
    public static IReadOnlyList<DynamicEndpointDifference> Compare(DynamicEndpointDefinition? from, DynamicEndpointDefinition? to)
    {
        var differences = new List<DynamicEndpointDifference>();
        CompareObjects(string.Empty, ToNode(from), ToNode(to), differences);
        return differences;
    }

    private static JsonObject ToNode(DynamicEndpointDefinition? definition)
    {
        if (definition is null)
        {
            return [];
        }

        var node = JsonSerializer.SerializeToNode(DefinitionNormalizer.Normalize(definition), DynamicEndpointsJson.SerializerOptions)!.AsObject();
        foreach (var name in Ignored)
        {
            node.Remove(name);
        }

        return node;
    }

    private static void Compare(string path, JsonNode? from, JsonNode? to, List<DynamicEndpointDifference> differences)
    {
        if (IsEmpty(from) && IsEmpty(to))
        {
            return;
        }

        if (IsEmpty(from))
        {
            differences.Add(new(path, DynamicEndpointDifferenceKind.Added, null, to!.DeepClone()));
        }
        else if (IsEmpty(to))
        {
            differences.Add(new(path, DynamicEndpointDifferenceKind.Removed, from!.DeepClone(), null));
        }
        else if (from is JsonObject fromObject && to is JsonObject toObject)
        {
            CompareObjects(path, fromObject, toObject, differences);
        }
        else if (from is JsonArray fromArray && to is JsonArray toArray)
        {
            CompareArrays(path, fromArray, toArray, differences);
        }
        else if (!JsonNode.DeepEquals(from, to))
        {
            differences.Add(new(path, DynamicEndpointDifferenceKind.Changed, from!.DeepClone(), to!.DeepClone()));
        }
    }

    private static void CompareObjects(string path, JsonObject from, JsonObject to, List<DynamicEndpointDifference> differences)
    {
        foreach (var (name, value) in from)
        {
            Compare(Join(path, name), value, to[name], differences);
        }

        foreach (var (name, value) in to)
        {
            if (!from.ContainsKey(name))
            {
                Compare(Join(path, name), null, value, differences);
            }
        }
    }

    private static void CompareArrays(string path, JsonArray from, JsonArray to, List<DynamicEndpointDifference> differences)
    {
        if (Names(from) is { } fromNames && Names(to) is { } toNames)
        {
            foreach (var (name, item) in fromNames)
            {
                Compare($"{path}[{name}]", item, toNames.GetValueOrDefault(name), differences);
            }

            foreach (var (name, item) in toNames)
            {
                if (!fromNames.ContainsKey(name))
                {
                    Compare($"{path}[{name}]", null, item, differences);
                }
            }

            return;
        }

        for (var i = 0; i < Math.Max(from.Count, to.Count); i++)
        {
            Compare($"{path}[{i}]", i < from.Count ? from[i] : null, i < to.Count ? to[i] : null, differences);
        }
    }

    // Items keyed by their "name" when every item has a unique one – a reordered or inserted parameter isn't "everything changed".
    private static Dictionary<string, JsonNode>? Names(JsonArray array)
    {
        var names = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var item in array)
        {
            if (item is not JsonObject obj || obj["name"] is not JsonValue value || !value.TryGetValue<string>(out var name) ||
                string.IsNullOrEmpty(name) || !names.TryAdd(name, item))
            {
                return null;
            }
        }

        return names.Count > 0 ? names : null;
    }

    // Missing, null, [] and {} mean the same in a definition.
    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => false,
    };

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";
}
