using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
    /// Tenant the endpoint belongs to (multi-tenancy, see <c>UseMultiTenancy()</c>). It is routable only for requests resolved to
    /// this tenant; <c>null</c> makes it a shared endpoint, routable for every tenant.
    /// </summary>
    public string? Tenant { get; init; }

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

    /// <summary>
    /// Example request body shown in the OpenAPI document (keyed by the names used in the request). When empty, it is composed
    /// from the <see cref="ParameterDefinition.Example"/> values of the body or form parameters.
    /// </summary>
    public JsonObject? RequestExample { get; init; }

    /// <summary>Example of a successful response shown in the OpenAPI document.</summary>
    public JsonNode? ResponseExample { get; init; }

    /// <summary>Allows anonymous access even when the application has a fallback authorization policy.</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>Requires an authenticated user (default policy).</summary>
    public bool RequireAuthorization { get; init; }

    /// <summary>Name of an authorization policy that must be satisfied. Implies <see cref="RequireAuthorization"/>.</summary>
    public string? AuthorizationPolicy { get; init; }

    /// <summary>Name of a rate limiting policy registered with <c>AddRateLimiter</c>. For limits defined here, use <see cref="RateLimit"/>.</summary>
    public string? RateLimitingPolicy { get; init; }

    /// <summary>Rate limit and quota defined in the endpoint itself (instead of <see cref="RateLimitingPolicy"/>).</summary>
    public DynamicEndpointRateLimit? RateLimit { get; init; }

    /// <summary><c>Cache-Control</c>, ETags and server-side output caching of responses.</summary>
    public DynamicEndpointCaching? Caching { get; init; }

    /// <summary>
    /// Where the definition came from, e.g. the OpenAPI operation it was imported from – a re-import updates (or a sync deletes)
    /// only endpoints with its origin. <c>null</c> for endpoints created by hand.
    /// </summary>
    public DynamicEndpointOrigin? Origin { get; init; }

    /// <summary>Disabled endpoints are persisted but not routable.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Revision of the definition – an optimistic concurrency token managed by the library (1 on create, +1 on every change).
    /// Updates must send the revision they are based on. Not related to API versioning.
    /// </summary>
    public int Revision { get; init; }

    /// <summary>Former name of <see cref="Revision"/>.</summary>
    [Obsolete("Renamed to Revision – it is an optimistic concurrency token, not an API version.")]
    [JsonIgnore]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int Version { get => Revision; init => Revision = value; }

    // Definitions saved or sent by 0.1.x use "version" – still accepted when reading JSON, never written.
    [JsonInclude]
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    private int? LegacyVersion
    {
        get => null;
        init
        {
            if (value is { } revision && Revision == 0)
            {
                Revision = revision;
            }
        }
    }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
