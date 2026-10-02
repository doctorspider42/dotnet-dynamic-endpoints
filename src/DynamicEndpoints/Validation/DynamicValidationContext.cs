using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>Input of a custom validator.</summary>
public sealed class DynamicValidationContext
{
    private readonly CompiledEndpoint _endpoint;
    private readonly CompiledValidator _validator;
    private readonly ValidationErrors _errors;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<IFormFile>> _files;
    private readonly DynamicRequestItems _items;
    private JsonObject? _configuration;

    internal DynamicValidationContext(
        CompiledEndpoint endpoint,
        CompiledValidator validator,
        ParameterDefinition? parameter,
        JsonObject parameters,
        IReadOnlyDictionary<string, IReadOnlyList<IFormFile>> files,
        HttpContext httpContext,
        ValidationErrors errors,
        DynamicRequestItems items)
    {
        _endpoint = endpoint;
        _items = items;
        _validator = validator;
        _errors = errors;
        _files = files;
        Parameter = parameter;
        Parameters = parameters;
        HttpContext = httpContext;
    }

    public DynamicEndpointDefinition Endpoint => _endpoint.Definition;

    /// <summary>The endpoint being served, as attached to the routed endpoint.</summary>
    public DynamicEndpointMetadata Metadata => _endpoint.Metadata;

    /// <summary>
    /// Per-request storage shared by all validators, filters and the processor (<see cref="DynamicRequest.Items"/>) – hand over
    /// work you already did, e.g. a loaded entity, so nobody repeats it.
    /// </summary>
    public IDictionary<object, object?> Items => _items.Values;

    /// <summary>Name the validator is registered under.</summary>
    public string ValidatorName => _validator.Name;

    /// <summary>The parameter being validated, or <c>null</c> for request-level validators.</summary>
    public ParameterDefinition? Parameter { get; }

    /// <summary>Value of <see cref="Parameter"/> (already converted to its JSON type).</summary>
    public JsonNode? Value => Parameter is null ? null : Parameters[Parameter.Name];

    /// <summary>All bound parameters keyed by <see cref="ParameterDefinition.Name"/>. Treat as read-only.</summary>
    public JsonObject Parameters { get; }

    public HttpContext HttpContext { get; }

    public IServiceProvider Services => HttpContext.RequestServices;

    public CancellationToken RequestAborted => HttpContext.RequestAborted;

    /// <summary>Copy of the validator configuration from the definition.</summary>
    public JsonObject Configuration => _configuration ??= (JsonObject)_validator.Configuration.DeepClone();

    public T? GetValue<T>() => Value is null ? default : Value.Deserialize<T>(DynamicEndpointsJson.SerializerOptions);

    public T? Get<T>(string name) =>
        Parameters[name] is { } node ? node.Deserialize<T>(DynamicEndpointsJson.SerializerOptions) : default;

    /// <summary>The uploaded file of a <see cref="ParameterType.File"/> parameter (default: the validated one).</summary>
    public IFormFile? GetFile(string? name = null) => GetFiles(name) is [var first, ..] ? first : null;

    /// <summary>All uploaded files of a file parameter (default: the validated one).</summary>
    public IReadOnlyList<IFormFile> GetFiles(string? name = null) =>
        (name ?? Parameter?.Name) is { } key && _files.TryGetValue(key, out var files) ? files : [];

    /// <summary>Configuration deserialized to <typeparamref name="T"/>, cached per endpoint version – treat as read-only.</summary>
    public T? GetConfiguration<T>() =>
        (T?)_validator.ConfigurationCache.GetOrAdd(
            typeof(T),
            static (_, config) => config.Deserialize<T>(DynamicEndpointsJson.SerializerOptions),
            _validator.Configuration);

    /// <summary>
    /// Hands the parsed form of a parameter (default: the validated one) on to later validators and the processor, which read it
    /// with <see cref="DynamicRequest.GetParsedValue{T}"/> – e.g. a decoded document, so it is decoded only once.
    /// </summary>
    public void SetParsedValue(object? value, string? parameterName = null)
    {
        var name = parameterName ?? Parameter?.Name
            ?? throw new InvalidOperationException("Request-level validators must name the parameter.");
        if (!_endpoint.ParametersByName.ContainsKey(name))
        {
            throw new ArgumentException($"The endpoint has no parameter '{name}'.", nameof(parameterName));
        }

        _items.Parsed[name] = value;
    }

    /// <summary>A value an earlier validator handed over with <see cref="SetParsedValue"/>.</summary>
    public bool TryGetParsedValue<T>(string name, out T? value) => _items.TryGetParsed(name, out value);

    /// <summary>Reports an error for the validated parameter (or for the request as a whole).</summary>
    public void AddError(string message) => AddError(null, message);

    /// <summary>
    /// Reports an error with a machine readable <paramref name="code"/> (default <see cref="DynamicValidationCodes.Custom"/>),
    /// exposed through <see cref="DynamicValidationFailedContext.Errors"/>. See <see cref="AddError(string?, string)"/> for paths.
    /// </summary>
    public void AddError(string? path, string message, string code)
    {
        if (string.IsNullOrEmpty(path))
        {
            _errors.Add(Parameter?.EffectiveSourceName ?? "request", message, code);
            return;
        }

        var end = path.IndexOfAny(['.', '[']);
        var head = end < 0 ? path : path[..end];
        var parameter = _endpoint.Parameters
            .FirstOrDefault(p => string.Equals(p.Definition.Name, head, StringComparison.OrdinalIgnoreCase))?.Definition;
        _errors.Add(parameter is null ? path : parameter.EffectiveSourceName + (end < 0 ? string.Empty : path[end..]), message, code);
    }

    /// <summary>
    /// Reports an error for a parameter path such as <c>checkOut</c>, <c>address.city</c> or <c>items[2].sku</c>.
    /// The first segment is matched against parameter names (case-insensitive) and reported under the name used in the request.
    /// </summary>
    public void AddError(string? path, string message) => AddError(path, message, DynamicValidationCodes.Custom);
}
