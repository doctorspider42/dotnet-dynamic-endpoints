using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>
/// Hooks into the request pipeline of every dynamic endpoint. Register with <c>AddFilter&lt;T&gt;()</c>; filters are resolved
/// from the request scope and run in registration order. Implement only the methods you need.
/// </summary>
public interface IDynamicEndpointFilter
{
    /// <summary>
    /// Runs after routing and authorization, but before the request is read or validated – the place for access checks
    /// (e.g. "does this tenant have this feature?"). Set <see cref="DynamicEndpointFilterContext.Result"/> to answer the request
    /// right away (e.g. with a 403); the remaining filters, validation and the processor are then skipped.
    /// </summary>
    ValueTask OnRequestAsync(DynamicEndpointRequestContext context) => ValueTask.CompletedTask;

    /// <summary>
    /// Runs whenever the library rejects a request: validation errors, a malformed or undecodable body (400),
    /// a body that is too large (413) or of an unsupported media type (415). <see cref="DynamicEndpointFilterContext.Result"/>
    /// holds the default problem response – replace it to use your own error format. Every filter runs, so this is also
    /// the place for logging and metering of rejected requests.
    /// </summary>
    ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context) => ValueTask.CompletedTask;
}

public abstract class DynamicEndpointFilterContext
{
    private protected DynamicEndpointFilterContext(HttpContext httpContext, DynamicEndpointMetadata endpoint)
    {
        HttpContext = httpContext;
        Endpoint = endpoint;
    }

    public HttpContext HttpContext { get; }

    /// <summary>The endpoint being called, including its complete definition.</summary>
    public DynamicEndpointMetadata Endpoint { get; }

    public IServiceProvider Services => HttpContext.RequestServices;

    public CancellationToken RequestAborted => HttpContext.RequestAborted;

    /// <summary>The response to send.</summary>
    public IResult? Result { get; set; }
}

/// <summary>Input of <see cref="IDynamicEndpointFilter.OnRequestAsync"/>. <see cref="DynamicEndpointFilterContext.Result"/> starts empty.</summary>
public sealed class DynamicEndpointRequestContext : DynamicEndpointFilterContext
{
    internal DynamicEndpointRequestContext(HttpContext httpContext, DynamicEndpointMetadata endpoint)
        : base(httpContext, endpoint)
    {
    }
}

/// <summary>Why the library rejected a request.</summary>
public enum DynamicRequestRejection
{
    /// <summary>One or more parameters, rules or validators failed (400).</summary>
    Validation,
    /// <summary>The body is not valid JSON / form data, not a JSON object or not valid UTF-8 (400).</summary>
    InvalidBody,
    /// <summary>The body exceeds the configured size limit (413).</summary>
    PayloadTooLarge,
    /// <summary>The body has a content type the endpoint does not read (415).</summary>
    UnsupportedMediaType,
}

/// <summary>Input of <see cref="IDynamicEndpointFilter.OnValidationFailedAsync"/>.</summary>
public sealed class DynamicValidationFailedContext : DynamicEndpointFilterContext
{
    internal DynamicValidationFailedContext(
        HttpContext httpContext,
        DynamicEndpointMetadata endpoint,
        DynamicRequestRejection reason,
        int statusCode,
        string title,
        string? detail,
        IReadOnlyList<DynamicValidationError> errors,
        JsonObject parameters,
        IResult result)
        : base(httpContext, endpoint)
    {
        Reason = reason;
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Errors = errors;
        Parameters = parameters;
        Result = result;
    }

    public DynamicRequestRejection Reason { get; }

    /// <summary>Status code of the default response: 400, 413 or 415.</summary>
    public int StatusCode { get; }

    /// <summary>Localized title of the default response.</summary>
    public string Title { get; }

    /// <summary>Localized explanation for 413/415 responses.</summary>
    public string? Detail { get; }

    /// <summary>All errors in the order they were found, with stable codes. Empty for 413/415.</summary>
    public IReadOnlyList<DynamicValidationError> Errors { get; }

    /// <summary>Parameters that could be bound before the request was rejected (for logging). Treat as read-only.</summary>
    public JsonObject Parameters { get; }

    /// <summary><see cref="Errors"/> grouped by key – the shape of <c>ValidationProblemDetails.Errors</c>.</summary>
    public IDictionary<string, string[]> ToDictionary() =>
        Errors.GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
}

/// <summary>A filter built from delegates – see <c>AddFilter(onRequest, onValidationFailed)</c>.</summary>
internal sealed class DelegateFilter(
    Func<DynamicEndpointRequestContext, ValueTask>? onRequest,
    Func<DynamicValidationFailedContext, ValueTask>? onValidationFailed) : IDynamicEndpointFilter
{
    public ValueTask OnRequestAsync(DynamicEndpointRequestContext context) =>
        onRequest?.Invoke(context) ?? ValueTask.CompletedTask;

    public ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context) =>
        onValidationFailed?.Invoke(context) ?? ValueTask.CompletedTask;
}
