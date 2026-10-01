using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Processing;
using DynamicEndpoints.Validation;

namespace DynamicEndpoints;

/// <summary>
/// Fluent construction of <see cref="DynamicEndpointDefinition"/>s:
/// <code>
/// DynamicEndpoint.Post("/orders")
///     .Named("Create order")
///     .HandledBy("orders", new { queue = "incoming" })
///     .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
///     .Build();
/// </code>
/// </summary>
public sealed class DynamicEndpoint
{
    private DynamicEndpointDefinition _definition;
    private readonly List<ParameterDefinition> _parameters = [];
    private readonly List<ValidationRuleDefinition> _rules = [];

    private DynamicEndpoint(string method, string route) =>
        _definition = new DynamicEndpointDefinition { Method = method, Route = route };

    public static DynamicEndpoint Get(string route) => new("GET", route);
    public static DynamicEndpoint Post(string route) => new("POST", route);
    public static DynamicEndpoint Put(string route) => new("PUT", route);
    public static DynamicEndpoint Patch(string route) => new("PATCH", route);
    public static DynamicEndpoint Delete(string route) => new("DELETE", route);

    public DynamicEndpoint WithId(Guid id) => Set(d => d with { Id = id });
    public DynamicEndpoint Named(string name) => Set(d => d with { Name = name });
    public DynamicEndpoint WithDescription(string description) => Set(d => d with { Description = description });
    /// <summary>Section the endpoint is listed under in Swagger UI.</summary>
    public DynamicEndpoint InGroup(string group) => Set(d => d with { Group = group });
    public DynamicEndpoint Disabled() => Set(d => d with { Enabled = false });
    public DynamicEndpoint AllowAnonymous() => Set(d => d with { AllowAnonymous = true });
    public DynamicEndpoint RequireAuthorization(string? policy = null) =>
        Set(d => d with { RequireAuthorization = true, AuthorizationPolicy = policy });
    public DynamicEndpoint RequireRateLimiting(string policy) => Set(d => d with { RateLimitingPolicy = policy });
    public DynamicEndpoint WithResponseSchema(string jsonSchema) => Set(d => d with { ResponseSchema = ParseObject(jsonSchema) });

    /// <summary>Selects the processor; <paramref name="configuration"/> may be a <see cref="JsonObject"/>, JSON string or any serializable object.</summary>
    public DynamicEndpoint HandledBy(string processor, object? configuration = null) =>
        Set(d => d with { Processor = processor, ProcessorConfig = ToObject(configuration) });

    /// <summary>
    /// Selects the processor by type – the name is resolved the same way as during registration
    /// (<see cref="DynamicProcessorAttribute"/> or the type name), so no magic strings.
    /// </summary>
    public DynamicEndpoint HandledBy<TProcessor>(object? configuration = null)
        where TProcessor : IDynamicEndpointProcessor =>
        HandledBy(ProcessorRegistry.Describe(typeof(TProcessor), null, null).Name, configuration);

    /// <summary>Selects the processor by type with a strongly typed configuration.</summary>
    public DynamicEndpoint HandledBy<TProcessor, TConfiguration>(TConfiguration configuration)
        where TProcessor : DynamicEndpointProcessor<TConfiguration>
        where TConfiguration : class, new() =>
        HandledBy<TProcessor>(configuration);

    /// <summary>
    /// Attaches a request-level validator by type. Works for <see cref="IDynamicValidator"/> classes and
    /// FluentValidation validators alike (same naming rule as their registration).
    /// </summary>
    public DynamicEndpoint ValidatedBy<TValidator>(object? configuration = null)
        where TValidator : class =>
        ValidatedBy(ValidatorRegistry.Describe(typeof(TValidator), null, null).Name, configuration);

    /// <summary>Attaches a request-level validator by type with a strongly typed configuration.</summary>
    public DynamicEndpoint ValidatedBy<TValidator, TConfiguration>(TConfiguration configuration)
        where TValidator : DynamicValidator<TConfiguration>
        where TConfiguration : class, new() =>
        ValidatedBy<TValidator>(configuration);

    public DynamicEndpoint FromRoute(string name, Action<ParameterBuilder>? configure = null) => Parameter(name, ParameterSource.Route, configure, required: true);
    public DynamicEndpoint FromQuery(string name, Action<ParameterBuilder>? configure = null) => Parameter(name, ParameterSource.Query, configure);
    public DynamicEndpoint FromHeader(string name, Action<ParameterBuilder>? configure = null) => Parameter(name, ParameterSource.Header, configure);
    public DynamicEndpoint FromBody(string name, Action<ParameterBuilder>? configure = null) => Parameter(name, ParameterSource.Body, configure);

    /// <summary>Adds a JsonLogic business rule, e.g. <c>{"&lt;": [{"var": "from"}, {"var": "to"}]}</c>.</summary>
    public DynamicEndpoint WithRule(string condition, string message, string? parameter = null) =>
        WithRule(JsonNode.Parse(condition), message, parameter);

    /// <summary>Attaches a request-level custom validator (runs after parameters and rules passed).</summary>
    public DynamicEndpoint ValidatedBy(string validator, object? configuration = null) =>
        Set(d => d with { Validators = [.. d.Validators, new ValidatorReference { Name = validator, Config = ToObject(configuration) }] });

    public DynamicEndpoint WithRule(JsonNode? condition, string message, string? parameter = null)
    {
        _rules.Add(new ValidationRuleDefinition { Condition = condition, Message = message, Parameter = parameter });
        return this;
    }

    public DynamicEndpointDefinition Build() => _definition with { Parameters = [.. _parameters], Rules = [.. _rules] };

    public static implicit operator DynamicEndpointDefinition(DynamicEndpoint builder) => builder.Build();

    private DynamicEndpoint Parameter(string name, ParameterSource source, Action<ParameterBuilder>? configure, bool required = false)
    {
        var builder = new ParameterBuilder(new ParameterDefinition { Name = name, Source = source, Required = required });
        configure?.Invoke(builder);
        _parameters.Add(builder.Definition);
        return this;
    }

    private DynamicEndpoint Set(Func<DynamicEndpointDefinition, DynamicEndpointDefinition> change)
    {
        _definition = change(_definition);
        return this;
    }

    internal static JsonObject? ToObject(object? value) => value switch
    {
        null => null,
        JsonObject obj => obj,
        string json => ParseObject(json),
        _ => JsonSerializer.SerializeToNode(value, DynamicEndpointsJson.SerializerOptions) as JsonObject
            ?? throw new ArgumentException("Configuration must serialize to a JSON object.", nameof(value)),
    };

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Expected a JSON object.", nameof(json));
}

public sealed class ParameterBuilder
{
    internal ParameterBuilder(ParameterDefinition definition) => Definition = definition;

    internal ParameterDefinition Definition { get; private set; }

    public ParameterBuilder String() => Type(ParameterType.String);
    public ParameterBuilder Integer() => Type(ParameterType.Integer);
    public ParameterBuilder Number() => Type(ParameterType.Number);
    public ParameterBuilder Boolean() => Type(ParameterType.Boolean);
    public ParameterBuilder Date() => Type(ParameterType.Date);
    public ParameterBuilder DateTime() => Type(ParameterType.DateTime);
    public ParameterBuilder Guid() => Type(ParameterType.Guid);

    /// <summary>A string in a well-known format (e-mail, URI, phone, IP address, time).</summary>
    public ParameterBuilder Format(ParameterFormat format) => Set(p => p with { Format = format });
    public ParameterBuilder Email() => String().Format(ParameterFormat.Email);
    public ParameterBuilder Uri() => String().Format(ParameterFormat.Uri);
    public ParameterBuilder Phone() => String().Format(ParameterFormat.Phone);
    public ParameterBuilder ArrayOf(ParameterType itemType) => Set(p => p with { Type = ParameterType.Array, ItemType = itemType });

    /// <summary>JSON object (body only), optionally described by a JSON Schema.</summary>
    public ParameterBuilder Object(string? jsonSchema = null) =>
        Set(p => p with { Type = ParameterType.Object, Schema = jsonSchema is null ? null : DynamicEndpoint.ToObject(jsonSchema) });

    public ParameterBuilder Required(bool required = true) => Set(p => p with { Required = required });
    public ParameterBuilder Optional() => Required(false);
    public ParameterBuilder Description(string description) => Set(p => p with { Description = description });

    /// <summary>Name used in the HTTP request when it differs from the parameter name, e.g. <c>X-Tenant-Id</c>.</summary>
    public ParameterBuilder BindFrom(string sourceName) => Set(p => p with { SourceName = sourceName });

    public ParameterBuilder Default(object value) => Set(p => p with { Default = JsonSerializer.SerializeToNode(value) });
    public ParameterBuilder Example(object value) => Set(p => p with { Example = JsonSerializer.SerializeToNode(value) });
    public ParameterBuilder MinLength(int value) => Set(p => p with { MinLength = value });
    public ParameterBuilder MaxLength(int value) => Set(p => p with { MaxLength = value });
    public ParameterBuilder Length(int min, int max) => Set(p => p with { MinLength = min, MaxLength = max });
    public ParameterBuilder Min(decimal value) => Set(p => p with { Minimum = value });
    public ParameterBuilder Max(decimal value) => Set(p => p with { Maximum = value });
    public ParameterBuilder Range(decimal min, decimal max) => Set(p => p with { Minimum = min, Maximum = max });
    public ParameterBuilder Pattern(string regex) => Set(p => p with { Pattern = regex });
    public ParameterBuilder Items(int min, int max) => Set(p => p with { MinItems = min, MaxItems = max });
    public ParameterBuilder OneOf(params object[] values) =>
        Set(p => p with { AllowedValues = values.Select(v => JsonSerializer.SerializeToNode(v)).ToList() });

    /// <summary>Attaches a custom validator to this parameter.</summary>
    public ParameterBuilder ValidatedBy(string validator, object? configuration = null) => Set(p => p with
    {
        Validators = [.. p.Validators ?? [], new ValidatorReference { Name = validator, Config = DynamicEndpoint.ToObject(configuration) }],
    });

    /// <summary>Attaches a custom validator by type (an <see cref="IDynamicValidator"/> or a FluentValidation validator).</summary>
    public ParameterBuilder ValidatedBy<TValidator>(object? configuration = null)
        where TValidator : class =>
        ValidatedBy(ValidatorRegistry.Describe(typeof(TValidator), null, null).Name, configuration);

    private ParameterBuilder Type(ParameterType type) => Set(p => p with { Type = type });

    private ParameterBuilder Set(Func<ParameterDefinition, ParameterDefinition> change)
    {
        Definition = change(Definition);
        return this;
    }
}
