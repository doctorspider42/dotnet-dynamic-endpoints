using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>What went wrong – see <see cref="DynamicErrorContext.Kind"/>.</summary>
public enum DynamicErrorKind
{
    /// <summary>One or more parameters, rules or validators failed (400).</summary>
    Validation,
    /// <summary>The body is not valid JSON / form data, not a JSON object or not valid UTF-8 (400).</summary>
    InvalidBody,
    /// <summary>The body exceeds the configured size limit (413).</summary>
    PayloadTooLarge,
    /// <summary>The body has a content type the endpoint does not read (415).</summary>
    UnsupportedMediaType,
    /// <summary>An empty 401 response, e.g. from authentication.</summary>
    Unauthorized,
    /// <summary>An empty 403 response, e.g. from authorization.</summary>
    Forbidden,
    /// <summary>An empty 404 response – no route matched, or a processor answered <c>Results.NotFound()</c>.</summary>
    NotFound,
    /// <summary>An empty 405 response.</summary>
    MethodNotAllowed,
    /// <summary>An unhandled exception (500). <see cref="DynamicErrorContext.Exception"/> holds it – never send it to clients.</summary>
    Exception,
    /// <summary>Any other empty error response (4xx/5xx).</summary>
    Other,
}

/// <summary>Everything known about an error response that is about to be sent.</summary>
public sealed class DynamicErrorContext
{
    internal DynamicErrorContext(
        HttpContext httpContext,
        DynamicErrorKind kind,
        int statusCode,
        string title,
        string? detail,
        IReadOnlyList<DynamicValidationError> errors,
        Exception? exception)
    {
        HttpContext = httpContext;
        Kind = kind;
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Errors = errors;
        Exception = exception;
    }

    public HttpContext HttpContext { get; }

    public DynamicErrorKind Kind { get; }

    public int StatusCode { get; }

    /// <summary>Localized title (see <see cref="DynamicEndpointsOptions.Messages"/>).</summary>
    public string Title { get; }

    /// <summary>Localized explanation for 413/415 responses.</summary>
    public string? Detail { get; }

    /// <summary>Validation errors with stable codes; empty for errors other than 400.</summary>
    public IReadOnlyList<DynamicValidationError> Errors { get; }

    /// <summary>The unhandled exception of <see cref="DynamicErrorKind.Exception"/>. For logging only.</summary>
    public Exception? Exception { get; }

    /// <summary>The dynamic endpoint that was called; <c>null</c> for requests no dynamic endpoint matched.</summary>
    public DynamicEndpointMetadata? Endpoint => HttpContext.GetDynamicEndpoint();

    /// <summary>Correlation id for the response: the current trace id, or <see cref="HttpContext.TraceIdentifier"/>.</summary>
    public string RequestId => Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier;

    /// <summary><see cref="Errors"/> grouped by key – the shape of <c>ValidationProblemDetails.Errors</c>.</summary>
    public IDictionary<string, string[]> ToDictionary() =>
        Errors.GroupBy(e => e.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray(), StringComparer.Ordinal);
}

/// <summary>
/// Builds every error response of dynamic endpoints in one place – validation errors, 413/415, and (with
/// <c>app.UseDynamicEndpointsErrorResponses()</c>) empty 401/403/404/405 responses and unhandled exceptions too.
/// Register with <c>UseErrorResponseFactory&lt;T&gt;()</c> or inline with <c>UseErrorResponses(context =&gt; …)</c>.
/// <see cref="IDynamicEndpointFilter.OnValidationFailedAsync"/> still runs afterwards and can replace the result.
/// </summary>
public interface IDynamicErrorResponseFactory
{
    IResult CreateResponse(DynamicErrorContext context);
}

/// <summary>The default: RFC 9457 problem details (<c>ValidationProblem</c> for 400).</summary>
public sealed class DefaultDynamicErrorResponseFactory : IDynamicErrorResponseFactory
{
    public IResult CreateResponse(DynamicErrorContext context) =>
        context.StatusCode == StatusCodes.Status400BadRequest
            ? Results.ValidationProblem(context.ToDictionary(), title: context.Title)
            : Results.Problem(statusCode: context.StatusCode, title: context.Title, detail: context.Detail);
}

internal sealed class DelegateErrorResponseFactory(Func<DynamicErrorContext, IResult> factory) : IDynamicErrorResponseFactory
{
    public IResult CreateResponse(DynamicErrorContext context) => factory(context);
}
