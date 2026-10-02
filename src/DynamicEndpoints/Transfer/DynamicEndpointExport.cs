using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DynamicEndpoints;

/// <summary>
/// A set of definitions in a stable, diff-friendly form – for backups, promotion between environments and GitOps. Revisions and
/// timestamps are left out (they belong to a store), endpoints are sorted by route, method and id.
/// </summary>
/// <example>
/// <code>
/// {
///   "format": "dynamic-endpoints/v1",
///   "endpoints": [ { "id": "…", "method": "GET", "route": "/orders/{id}", … } ]
/// }
/// </code>
/// </example>
public sealed record DynamicEndpointExport
{
    public const string CurrentFormat = "dynamic-endpoints/v1";

    private static readonly string[] StoreProperties = ["revision", "createdAt", "updatedAt", "version"];

    public string Format { get; init; } = CurrentFormat;

    public IReadOnlyList<DynamicEndpointDefinition> Endpoints { get; init; } = [];

    /// <summary>The export as JSON, without store-managed properties. Use an <see cref="IDynamicEndpointsTextFormat"/> to write it.</summary>
    public JsonObject ToJsonNode()
    {
        var endpoints = new JsonArray();
        foreach (var definition in Sort(Endpoints))
        {
            var node = (JsonObject)JsonSerializer.SerializeToNode(definition, DynamicEndpointsJson.SerializerOptions)!;
            foreach (var property in StoreProperties)
            {
                node.Remove(property);
            }

            endpoints.Add(node);
        }

        return new JsonObject { ["format"] = Format, ["endpoints"] = endpoints };
    }

    /// <summary>Indented JSON with <c>\n</c> line endings and a trailing newline.</summary>
    public string ToJson() => new JsonDynamicEndpointsTextFormat().Write(ToJsonNode());

    /// <summary>Reads an export – or a plain array of definitions. Revisions and timestamps in the input are ignored.</summary>
    /// <exception cref="FormatException">The document is not an export of a supported format.</exception>
    public static DynamicEndpointExport FromJsonNode(JsonNode? node)
    {
        JsonArray? endpoints;
        var format = CurrentFormat;
        switch (node)
        {
            case JsonArray array:
                endpoints = array;
                break;
            case JsonObject obj:
                if (obj["format"] is JsonValue value)
                {
                    format = value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;
                    if (format != CurrentFormat)
                    {
                        throw new FormatException($"Unsupported format '{format}'. Expected '{CurrentFormat}'.");
                    }
                }

                endpoints = obj["endpoints"] as JsonArray ?? throw new FormatException("The document has no 'endpoints' array.");
                break;
            default:
                throw new FormatException("Expected an object with an 'endpoints' array, or an array of endpoint definitions.");
        }

        var definitions = new List<DynamicEndpointDefinition>(endpoints.Count);
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (endpoints[i] is not JsonObject item)
            {
                throw new FormatException($"endpoints[{i}] is not an object.");
            }

            var clean = (JsonObject)item.DeepClone();
            foreach (var property in StoreProperties)
            {
                clean.Remove(property);
            }

            try
            {
                definitions.Add(clean.Deserialize<DynamicEndpointDefinition>(DynamicEndpointsJson.SerializerOptions)
                    ?? throw new FormatException($"endpoints[{i}] is empty."));
            }
            catch (JsonException ex)
            {
                throw new FormatException($"endpoints[{i}] is not a valid definition: {ex.Message}", ex);
            }
        }

        return new DynamicEndpointExport { Format = format, Endpoints = definitions };
    }

    /// <inheritdoc cref="FromJsonNode"/>
    public static DynamicEndpointExport FromJson(string json) => FromJsonNode(new JsonDynamicEndpointsTextFormat().Parse(json));

    internal static IEnumerable<DynamicEndpointDefinition> Sort(IEnumerable<DynamicEndpointDefinition> definitions) =>
        definitions
            .OrderBy(d => d.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Route, StringComparer.Ordinal)
            .ThenBy(d => d.Method, StringComparer.Ordinal)
            .ThenBy(d => d.Id);
}

/// <summary>How an import treats the definitions that already exist.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointImportMode>))]
public enum DynamicEndpointImportMode
{
    /// <summary>Only creates endpoints that don't exist yet; existing ones are skipped.</summary>
    Create,
    /// <summary>Creates new endpoints and replaces existing ones (default).</summary>
    Upsert,
    /// <summary>Like <see cref="Upsert"/>, and deletes every endpoint that is not in the import – the store mirrors the file.</summary>
    Sync,
}

public sealed record DynamicEndpointImportOptions
{
    public DynamicEndpointImportMode Mode { get; init; } = DynamicEndpointImportMode.Upsert;

    /// <summary>Reports what would happen – with full validation, including route conflicts within the import – and writes nothing.</summary>
    public bool DryRun { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointImportAction>))]
public enum DynamicEndpointImportAction
{
    Create,
    Update,
    Delete,
    Unchanged,
    /// <summary>Exists already and the mode is <see cref="DynamicEndpointImportMode.Create"/>.</summary>
    Skip,
    /// <summary>Failed validation (see <see cref="DynamicEndpointImportItem.Errors"/>) – nothing was written.</summary>
    Invalid,
}

/// <summary>What an import does (or would do) with one endpoint.</summary>
public sealed record DynamicEndpointImportItem
{
    public DynamicEndpointImportAction Action { get; init; }

    public Guid Id { get; init; }

    public string Method { get; init; } = "";

    public string Route { get; init; } = "";

    public string? Name { get; init; }

    /// <summary>Changed properties of an update, e.g. <c>parameters</c>, <c>processorConfig</c>.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

public sealed record DynamicEndpointImportResult
{
    /// <summary>Every endpoint was (or, in a dry run, would be) imported. When <c>false</c>, nothing was written.</summary>
    public bool Succeeded { get; init; }

    public bool DryRun { get; init; }

    public DynamicEndpointImportMode Mode { get; init; }

    public IReadOnlyList<DynamicEndpointImportItem> Items { get; init; } = [];

    public int Created => Count(DynamicEndpointImportAction.Create);

    public int Updated => Count(DynamicEndpointImportAction.Update);

    public int Deleted => Count(DynamicEndpointImportAction.Delete);

    public int Unchanged => Count(DynamicEndpointImportAction.Unchanged);

    public int Skipped => Count(DynamicEndpointImportAction.Skip);

    public int Invalid => Count(DynamicEndpointImportAction.Invalid);

    /// <summary>Something is (or would be) created, updated or deleted.</summary>
    public bool HasChanges => Created + Updated + Deleted > 0;

    private int Count(DynamicEndpointImportAction action) => Items.Count(i => i.Action == action);
}
