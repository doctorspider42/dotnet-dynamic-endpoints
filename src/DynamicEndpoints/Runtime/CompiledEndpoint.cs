using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DynamicEndpoints.Runtime;

/// <summary>Everything needed to serve a definition, prepared once when the definition is saved or loaded.</summary>
internal sealed class CompiledEndpoint
{
    public required DynamicEndpointDefinition Definition { get; init; }

    public required string ProcessorName { get; init; }

    public required RoutePattern RoutePattern { get; init; }

    /// <summary>"GET /orders/{}" – used for conflict detection.</summary>
    public required string RouteKey { get; init; }

    public required IReadOnlyList<CompiledParameter> Parameters { get; init; }

    public required IReadOnlyDictionary<string, CompiledParameter> ParametersByName { get; init; }

    /// <summary>JSON Schema of the parameter object (patterns excluded – those use <see cref="CompiledParameter.Pattern"/>).</summary>
    public required JsonObject ValidationSchema { get; init; }

    public required IReadOnlyList<CompiledRule> Rules { get; init; }

    /// <summary>Request-level custom validators.</summary>
    public required IReadOnlyList<CompiledValidator> Validators { get; init; }

    public required JsonObject Configuration { get; init; }

    public bool HasBody => Parameters.Any(p => p.Definition.Source == ParameterSource.Body);

    public ConcurrentDictionary<Type, object?> ConfigurationCache { get; } = new();
}

internal sealed record CompiledParameter(ParameterDefinition Definition, Regex? Pattern)
{
    public IReadOnlyList<CompiledValidator> Validators { get; init; } = [];
}

/// <summary>A validator reference resolved to its canonical registered name (the keyed service key).</summary>
internal sealed record CompiledValidator(string Name, JsonObject Configuration)
{
    public ConcurrentDictionary<Type, object?> ConfigurationCache { get; } = new();
}

internal sealed record CompiledRule(ValidationRuleDefinition Definition, JsonNode Condition);
