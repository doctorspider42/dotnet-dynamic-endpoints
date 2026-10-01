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
    private JsonObject? _configuration;

    internal DynamicValidationContext(
        CompiledEndpoint endpoint,
        CompiledValidator validator,
        ParameterDefinition? parameter,
        JsonObject parameters,
        HttpContext httpContext,
        ValidationErrors errors)
    {
        _endpoint = endpoint;
        _validator = validator;
        _errors = errors;
        Parameter = parameter;
        Parameters = parameters;
        HttpContext = httpContext;
    }

    public DynamicEndpointDefinition Endpoint => _endpoint.Definition;

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

    /// <summary>Configuration deserialized to <typeparamref name="T"/>, cached per endpoint version – treat as read-only.</summary>
    public T? GetConfiguration<T>() =>
        (T?)_validator.ConfigurationCache.GetOrAdd(
            typeof(T),
            static (_, config) => config.Deserialize<T>(DynamicEndpointsJson.SerializerOptions),
            _validator.Configuration);

    /// <summary>Reports an error for the validated parameter (or for the request as a whole).</summary>
    public void AddError(string message) => AddError(null, message);

    /// <summary>
    /// Reports an error for a parameter path such as <c>checkOut</c>, <c>address.city</c> or <c>items[2].sku</c>.
    /// The first segment is matched against parameter names (case-insensitive) and reported under the name used in the request.
    /// </summary>
    public void AddError(string? path, string message)
    {
        if (string.IsNullOrEmpty(path))
        {
            _errors.Add(Parameter?.EffectiveSourceName ?? "request", message);
            return;
        }

        var end = path.IndexOfAny(['.', '[']);
        var head = end < 0 ? path : path[..end];
        var parameter = _endpoint.Parameters
            .FirstOrDefault(p => string.Equals(p.Definition.Name, head, StringComparison.OrdinalIgnoreCase))?.Definition;
        _errors.Add(parameter is null ? path : parameter.EffectiveSourceName + (end < 0 ? string.Empty : path[end..]), message);
    }
}
