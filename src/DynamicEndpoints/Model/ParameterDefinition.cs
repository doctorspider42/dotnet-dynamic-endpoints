using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DynamicEndpoints;

/// <summary>Where a parameter value is read from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParameterSource>))]
public enum ParameterSource
{
    Route,
    Query,
    Header,
    /// <summary>A top-level property of a JSON object request body.</summary>
    Body,
}

/// <summary>Well-known formats of <see cref="ParameterType.String"/> parameters.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ParameterFormat>))]
public enum ParameterFormat
{
    /// <summary>E-mail address (pragmatic check: local@domain.tld, no comments or quoted parts).</summary>
    Email,
    /// <summary>Absolute URI with a scheme, e.g. <c>https://example.com/a?b=c</c>.</summary>
    Uri,
    /// <summary>Phone number in E.164 format, e.g. <c>+48123456789</c>.</summary>
    Phone,
    Ipv4,
    Ipv6,
    /// <summary>Time of day: <c>HH:mm</c> or <c>HH:mm:ss</c>.</summary>
    Time,
}

[JsonConverter(typeof(JsonStringEnumConverter<ParameterType>))]
public enum ParameterType
{
    String,
    Integer,
    Number,
    Boolean,
    /// <summary>ISO 8601 date (<c>2025-01-31</c>), carried as a string.</summary>
    Date,
    /// <summary>RFC 3339 date-time (<c>2025-01-31T12:00:00Z</c>), carried as a string.</summary>
    DateTime,
    Guid,
    Array,
    /// <summary>JSON object; only allowed for body parameters. Shape can be described with <see cref="ParameterDefinition.Schema"/>.</summary>
    Object,
}

/// <summary>
/// A single input of a dynamic endpoint. All parameters, regardless of their source,
/// end up in one flat JSON object passed to the processor under <see cref="Name"/>.
/// </summary>
public sealed record ParameterDefinition
{
    /// <summary>Name of the parameter inside the normalized parameter object (and in JsonLogic rules).</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Name used in the HTTP request (header name, query key, route parameter or body property)
    /// when it differs from <see cref="Name"/>, e.g. <c>X-Tenant-Id</c>.
    /// </summary>
    public string? SourceName { get; init; }

    public ParameterSource Source { get; init; } = ParameterSource.Query;

    public ParameterType Type { get; init; } = ParameterType.String;

    /// <summary>Type of array items when <see cref="Type"/> is <see cref="ParameterType.Array"/>.</summary>
    public ParameterType? ItemType { get; init; }

    public bool Required { get; init; }

    public string? Description { get; init; }

    /// <summary>Value used when the parameter is absent.</summary>
    public JsonNode? Default { get; init; }

    /// <summary>Example value shown in the OpenAPI document.</summary>
    public JsonNode? Example { get; init; }

    // Scalar constraints. For arrays they apply to every item.
    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    public decimal? Minimum { get; init; }

    public decimal? Maximum { get; init; }

    /// <summary>
    /// Regular expression the (string) value must contain a match for – anchor it with <c>^…$</c> for full matches.
    /// Evaluated with <c>RegexOptions.NonBacktracking</c>, so it is immune to ReDoS (backreferences and lookarounds are not supported).
    /// </summary>
    public string? Pattern { get; init; }

    /// <summary>Well-known format of a String parameter (or of the items of an array of strings).</summary>
    public ParameterFormat? Format { get; init; }

    /// <summary>Closed list of allowed values (JSON Schema <c>enum</c>).</summary>
    public IReadOnlyList<JsonNode?>? AllowedValues { get; init; }

    // Array constraints.
    public int? MinItems { get; init; }

    public int? MaxItems { get; init; }

    /// <summary>
    /// Custom JSON Schema (draft 2020-12) for <see cref="ParameterType.Object"/> and <see cref="ParameterType.Array"/> parameters.
    /// </summary>
    public JsonObject? Schema { get; init; }

    /// <summary>Custom validators run for this parameter when it is present and passed the built-in checks.</summary>
    public IReadOnlyList<ValidatorReference>? Validators { get; init; }

    [JsonIgnore]
    public string EffectiveSourceName => string.IsNullOrWhiteSpace(SourceName) ? Name : SourceName;
}
