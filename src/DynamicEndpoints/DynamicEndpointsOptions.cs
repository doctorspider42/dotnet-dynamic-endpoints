using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace DynamicEndpoints;

public sealed class DynamicEndpointsOptions
{
    /// <summary>
    /// Route prefixes dynamic endpoints may never use (e.g. <c>/admin</c>, <c>/swagger</c>).
    /// Prefixes passed to <c>MapDynamicEndpointsAdmin</c> are added automatically.
    /// Exact clashes with any other endpoint of the application are detected regardless of this list.
    /// </summary>
    public IList<string> ReservedPrefixes { get; } = new List<string>();

    /// <summary>
    /// When not empty, every route must start with one of these prefixes – checked when a definition is saved.
    /// Prefixes may contain parameters that match a single literal segment part: <c>/api/v{version:int}</c> accepts
    /// <c>/api/v1/orders</c> and <c>/api/v2/orders/{id}</c>, but neither <c>/orders</c> nor <c>/api/v{v}/orders</c>.
    /// </summary>
    public IList<string> RequiredRoutePrefixes { get; } = new List<string>();

    /// <summary>HTTP methods admins can use.</summary>
    public ISet<string> AllowedMethods { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GET", "POST", "PUT", "PATCH", "DELETE",
    };

    /// <summary>Processor used by definitions that do not name one – handy for the "single entry point" setup.</summary>
    public string? DefaultProcessor { get; set; }

    /// <summary>Maximum accepted JSON body size in bytes. Default 1 MB.</summary>
    public long MaxRequestBodySize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Maximum accepted size in bytes of form bodies (<c>multipart/form-data</c> with all its files, or
    /// <c>application/x-www-form-urlencoded</c>). Default 30 MB. The server's own limit (Kestrel: 30 MB) applies as well.
    /// </summary>
    public long MaxFormBodySize { get; set; } = 30 * 1024 * 1024;

    /// <summary>Maximum nesting depth of JSON bodies. Default 32.</summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    /// When set, every instance re-reads definitions from the store at this interval –
    /// the simplest way to propagate changes across a multi-instance deployment. With a change notifier
    /// (<c>UseChangeNotifier</c>, e.g. PostgreSQL <c>LISTEN/NOTIFY</c> or Redis pub/sub) changes arrive immediately and polling
    /// is only the fallback for lost messages, so a long interval (minutes) is enough.
    /// </summary>
    public TimeSpan? RefreshInterval { get; set; }

    /// <summary>
    /// How often drafts whose <see cref="DynamicEndpointDraft.PublishAt"/> has come are published. Default 10 seconds; <c>null</c>
    /// turns automatic publishing off (call <see cref="IDynamicEndpointManager.PublishDueAsync"/> from your own scheduler instead).
    /// With several instances every one checks – a draft is published exactly once.
    /// </summary>
    public TimeSpan? ScheduledPublishInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Identifies this instance in change notifications, so it ignores its own. Unique per process by default.</summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>Fail application start when definitions cannot be loaded from the store. Default <c>true</c>.</summary>
    public bool ThrowOnStartupLoadFailure { get; set; } = true;

    /// <summary>Hook to add custom metadata (CORS, output caching, …) to every built endpoint.</summary>
    public Action<EndpointBuilder, DynamicEndpointDefinition>? ConfigureEndpoint { get; set; }

    /// <summary>Texts of request validation errors: language selection, overrides, custom localization.</summary>
    public DynamicValidationMessages Messages { get; } = new();

    public DynamicEndpointsOpenApiOptions OpenApi { get; } = new();
}

public sealed class DynamicEndpointsOpenApiOptions
{
    public string Title { get; set; } = "Dynamic endpoints";

    public string Version { get; set; } = "v1";

    public string? Description { get; set; }

    /// <summary>Documentation section of endpoints without a <see cref="DynamicEndpointDefinition.Group"/>.</summary>
    public string DefaultGroup { get; set; } = "Dynamic";

    /// <summary>Security schemes added to <c>components.securitySchemes</c>, e.g. via <see cref="AddApiKey"/>.</summary>
    public IDictionary<string, JsonObject> SecuritySchemes { get; } = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    /// <summary>
    /// Document-level security requirements (<c>{ "ApiKey": [] }</c>). Prefer <see cref="OperationSecurity"/> (what
    /// <see cref="AddSecurityScheme"/> uses) – client generators see requirements attached to each operation more reliably.
    /// </summary>
    public IList<JsonObject> SecurityRequirements { get; } = new List<JsonObject>();

    /// <summary>
    /// Security requirements attached to each operation (or those selected by <see cref="DynamicOpenApiSecurityRequirement.AppliesTo"/>).
    /// Endpoints with <see cref="DynamicEndpointDefinition.AllowAnonymous"/> get none.
    /// </summary>
    public IList<DynamicOpenApiSecurityRequirement> OperationSecurity { get; } = new List<DynamicOpenApiSecurityRequirement>();

    /// <summary>Headers documented on every operation (or the ones selected by <see cref="DynamicOpenApiHeader.AppliesTo"/>).</summary>
    public IList<DynamicOpenApiHeader> Headers { get; } = new List<DynamicOpenApiHeader>();

    /// <summary>Last chance to change an operation – runs after everything else was generated.</summary>
    public Action<JsonObject, DynamicEndpointDefinition>? ConfigureOperation { get; set; }

    /// <summary>Last chance to change the whole document (servers, extra schemas, …) – runs after all operations were generated.</summary>
    public Action<JsonObject>? ConfigureDocument { get; set; }

    /// <summary>
    /// Adds a security scheme; with <paramref name="required"/> it is also attached to every operation – or to those matching
    /// <paramref name="appliesTo"/> – except anonymous ones.
    /// </summary>
    public DynamicEndpointsOpenApiOptions AddSecurityScheme(
        string name,
        JsonObject scheme,
        bool required = true,
        Func<DynamicEndpointDefinition, bool>? appliesTo = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scheme);
        SecuritySchemes[name] = scheme;
        if (required)
        {
            OperationSecurity.Add(new DynamicOpenApiSecurityRequirement(new JsonObject { [name] = new JsonArray() }) { AppliesTo = appliesTo });
        }

        return this;
    }

    /// <summary>
    /// Documents an API key sent in a header (e.g. <c>X-Api-Key</c>) and attaches it to every non-anonymous operation
    /// (or to those matching <paramref name="appliesTo"/>).
    /// </summary>
    public DynamicEndpointsOpenApiOptions AddApiKey(
        string headerName = "X-Api-Key",
        string schemeName = "ApiKey",
        string? description = null,
        Func<DynamicEndpointDefinition, bool>? appliesTo = null)
    {
        var scheme = new JsonObject { ["type"] = "apiKey", ["in"] = "header", ["name"] = headerName };
        if (description is not null)
        {
            scheme["description"] = description;
        }

        return AddSecurityScheme(schemeName, scheme, required: true, appliesTo);
    }

    /// <summary>Documents a header on every operation, or on those matching <paramref name="appliesTo"/>.</summary>
    public DynamicEndpointsOpenApiOptions AddHeader(
        string name,
        string? description = null,
        bool required = false,
        Func<DynamicEndpointDefinition, bool>? appliesTo = null,
        JsonObject? schema = null,
        JsonNode? example = null)
    {
        Headers.Add(new DynamicOpenApiHeader(name)
        {
            Description = description,
            Required = required,
            AppliesTo = appliesTo,
            Schema = schema,
            Example = example,
        });
        return this;
    }
}

/// <summary>A header documented on dynamic endpoint operations (handled by the application, e.g. by middleware).</summary>
public sealed class DynamicOpenApiHeader(string name)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A header name is required.", nameof(name)) : name;

    public string? Description { get; init; }

    public bool Required { get; init; }

    /// <summary>JSON Schema of the value. Default <c>{ "type": "string" }</c>.</summary>
    public JsonObject? Schema { get; init; }

    /// <summary>Example value, e.g. a UUID for <c>Idempotency-Key</c>.</summary>
    public JsonNode? Example { get; init; }

    /// <summary>Operations the header is documented on; all when <c>null</c>.</summary>
    public Func<DynamicEndpointDefinition, bool>? AppliesTo { get; init; }
}

/// <summary>A security requirement (<c>{ "ApiKey": [] }</c>) attached to dynamic endpoint operations.</summary>
public sealed class DynamicOpenApiSecurityRequirement(JsonObject requirement)
{
    public JsonObject Requirement { get; } = requirement ?? throw new ArgumentNullException(nameof(requirement));

    /// <summary>Operations the requirement is attached to; all non-anonymous ones when <c>null</c>.</summary>
    public Func<DynamicEndpointDefinition, bool>? AppliesTo { get; init; }
}
