using System.ComponentModel;
using System.Text.Json.Serialization;

namespace DynamicEndpoints;

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointStatus>))]
public enum DynamicEndpointStatus
{
    /// <summary>Routable on this instance.</summary>
    Active,
    /// <summary>Persisted, but switched off.</summary>
    Disabled,
    /// <summary>Persisted, but could not be activated (e.g. processor removed, route conflict).</summary>
    Invalid,
    /// <summary>Changed in the store, not yet picked up by this instance.</summary>
    Pending,
}

/// <summary>A persisted definition together with its runtime status on the current instance.</summary>
public sealed record DynamicEndpointState(
    DynamicEndpointDefinition Definition,
    DynamicEndpointStatus Status,
    IReadOnlyList<string> Errors);

public sealed record DynamicEndpointValidationResult(
    bool IsValid,
    IReadOnlyDictionary<string, string[]> Errors);

/// <summary>
/// Metadata attached to every routed dynamic endpoint – the complete, already validated definition, so middleware and filters
/// never need to query the store: <c>context.GetEndpoint()?.Metadata.GetMetadata&lt;DynamicEndpointMetadata&gt;()</c>.
/// </summary>
public sealed class DynamicEndpointMetadata
{
    private readonly Dictionary<string, ParameterDefinition> _parametersByName;

    internal DynamicEndpointMetadata(DynamicEndpointDefinition definition, string processorName)
    {
        Definition = definition;
        ProcessorName = processorName;
        _parametersByName = definition.Parameters.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public Guid Id => Definition.Id;

    /// <summary>Revision of the definition that is being served.</summary>
    public int Revision => Definition.Revision;

    public string? Name => Definition.Name;

    /// <summary>The normalized definition being served. Shared by all requests – treat it as read-only.</summary>
    public DynamicEndpointDefinition Definition { get; }

    /// <summary>Registered name of the processor that handles the endpoint (the default processor already resolved).</summary>
    public string ProcessorName { get; }

    public IReadOnlyList<ParameterDefinition> Parameters => Definition.Parameters;

    /// <summary>The endpoint reads a JSON body.</summary>
    public bool HasJsonBody => Definition.Parameters.Any(p => p.Source == ParameterSource.Body);

    /// <summary>The endpoint reads a form body (<c>multipart/form-data</c> or <c>application/x-www-form-urlencoded</c>).</summary>
    public bool HasFormBody => Definition.Parameters.Any(p => p.Source == ParameterSource.Form);

    /// <summary>The endpoint accepts file uploads.</summary>
    public bool HasFiles => Definition.Parameters.Any(IsFile);

    /// <summary>Parameter by <see cref="ParameterDefinition.Name"/>.</summary>
    public ParameterDefinition? FindParameter(string name) => _parametersByName.GetValueOrDefault(name);

    [Obsolete("Renamed to Revision – it is an optimistic concurrency token, not an API version.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Version => Revision;

    internal static bool IsFile(ParameterDefinition p) =>
        p.Type == ParameterType.File || (p.Type == ParameterType.Array && p.ItemType == ParameterType.File);
}
