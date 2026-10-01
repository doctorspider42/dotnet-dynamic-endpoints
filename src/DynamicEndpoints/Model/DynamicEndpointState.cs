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

/// <summary>Metadata attached to every routed dynamic endpoint – handy for middleware and diagnostics.</summary>
public sealed record DynamicEndpointMetadata(Guid Id, int Version, string? Name);
