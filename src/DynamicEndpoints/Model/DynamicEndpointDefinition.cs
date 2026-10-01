using System.Text.Json.Nodes;

namespace DynamicEndpoints;

/// <summary>
/// Declarative description of an HTTP endpoint that is created, changed and removed at runtime.
/// </summary>
public sealed record DynamicEndpointDefinition
{
    /// <summary>Stable identifier. Leave empty when creating – one is generated.</summary>
    public Guid Id { get; init; }

    /// <summary>HTTP method: GET, POST, PUT, PATCH or DELETE.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>ASP.NET Core route template, e.g. <c>/orders/{id}</c>.</summary>
    public string Route { get; init; } = "/";

    /// <summary>Human readable name, used as OpenAPI summary.</summary>
    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Section the endpoint is listed under in the documentation (Swagger UI).
    /// Falls back to <see cref="DynamicEndpointsOpenApiOptions.DefaultGroup"/>.
    /// </summary>
    public string? Group { get; init; }

    /// <summary>
    /// Name of a registered <see cref="IDynamicEndpointProcessor"/> that handles validated requests.
    /// Falls back to <see cref="DynamicEndpointsOptions.DefaultProcessor"/> when empty.
    /// </summary>
    public string? Processor { get; init; }

    /// <summary>Processor specific configuration, validated by the processor when the definition is saved.</summary>
    public JsonObject? ProcessorConfig { get; init; }

    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = [];

    /// <summary>Cross-field business rules evaluated after structural validation succeeded.</summary>
    public IReadOnlyList<ValidationRuleDefinition> Rules { get; init; } = [];

    /// <summary>
    /// Custom request-level validators. They run last – only when parameters and rules passed –
    /// so they may do expensive work such as database lookups.
    /// </summary>
    public IReadOnlyList<ValidatorReference> Validators { get; init; } = [];

    /// <summary>Optional JSON Schema of a successful response – used for documentation only.</summary>
    public JsonObject? ResponseSchema { get; init; }

    /// <summary>Allows anonymous access even when the application has a fallback authorization policy.</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Requires an authenticated user (default policy).</summary>
    public bool RequireAuthorization { get; init; }

    /// <summary>Name of an authorization policy that must be satisfied. Implies <see cref="RequireAuthorization"/>.</summary>
    public string? AuthorizationPolicy { get; init; }

    /// <summary>Name of a rate limiting policy registered with <c>AddRateLimiter</c>.</summary>
    public string? RateLimitingPolicy { get; init; }

    /// <summary>Disabled endpoints are persisted but not routable.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Optimistic concurrency version, managed by the library.</summary>
    public int Version { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
