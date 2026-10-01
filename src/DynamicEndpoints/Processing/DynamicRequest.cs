using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>A bound and validated request of a dynamic endpoint.</summary>
public sealed class DynamicRequest
{
    private readonly CompiledEndpoint _endpoint;
    private JsonObject? _configuration;

    internal DynamicRequest(CompiledEndpoint endpoint, JsonObject parameters, HttpContext httpContext)
    {
        _endpoint = endpoint;
        Parameters = parameters;
        HttpContext = httpContext;
    }

    /// <summary>Definition (and version) that handled this request.</summary>
    public DynamicEndpointDefinition Endpoint => _endpoint.Definition;

    public string ProcessorName => _endpoint.ProcessorName;

    /// <summary>
    /// Validated parameters keyed by <see cref="ParameterDefinition.Name"/>, already converted to their JSON types
    /// and with defaults applied. Absent optional parameters are not present.
    /// </summary>
    public JsonObject Parameters { get; }

    /// <summary>Copy of the processor configuration of the endpoint.</summary>
    public JsonObject Configuration => _configuration ??= (JsonObject)_endpoint.Configuration.DeepClone();

    public HttpContext HttpContext { get; }

    public IServiceProvider Services => HttpContext.RequestServices;

    public CancellationToken RequestAborted => HttpContext.RequestAborted;

    public bool Has(string name) => Parameters.ContainsKey(name);

    public T? Get<T>(string name) =>
        Parameters.TryGetPropertyValue(name, out var node) && node is not null
            ? node.Deserialize<T>(DynamicEndpointsJson.SerializerOptions)
            : default;

    public T GetRequired<T>(string name) =>
        Get<T>(name) ?? throw new InvalidOperationException($"Parameter '{name}' has no value.");

    /// <summary>
    /// Processor configuration deserialized to <typeparamref name="T"/>. Cached per endpoint version –
    /// treat the returned instance as read-only.
    /// </summary>
    public T? GetConfiguration<T>() =>
        (T?)_endpoint.ConfigurationCache.GetOrAdd(
            typeof(T),
            static (_, config) => config.Deserialize<T>(DynamicEndpointsJson.SerializerOptions),
            _endpoint.Configuration);
}
