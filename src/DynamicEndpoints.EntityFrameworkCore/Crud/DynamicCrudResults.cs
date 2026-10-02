using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>Error responses of <c>ef-crud</c> endpoints – built by the application's <see cref="IDynamicErrorResponseFactory"/>.</summary>
internal static class DynamicCrudResults
{
    public static IResult Invalid(DynamicRequest request, IReadOnlyList<DynamicValidationError> errors) =>
        Create(request, DynamicErrorKind.Validation, StatusCodes.Status400BadRequest, Title(request, "title.validation"), errors);

    public static IResult NotFound(DynamicRequest request) =>
        Create(request, DynamicErrorKind.NotFound, StatusCodes.Status404NotFound, Title(request, "title.notFound"));

    public static IResult NoTenant(DynamicRequest request) =>
        Create(request, DynamicErrorKind.NotFound, StatusCodes.Status404NotFound, "The request has no tenant.");

    public static IResult PreconditionFailed(DynamicRequest request) =>
        Create(request, DynamicErrorKind.Other, StatusCodes.Status412PreconditionFailed, "The entity was changed – If-Match doesn't match its current ETag.");

    public static IResult PreconditionRequired(DynamicRequest request) =>
        Create(request, DynamicErrorKind.Other, StatusCodes.Status428PreconditionRequired, "Send the entity's ETag in If-Match.");

    public static IResult Conflict(DynamicRequest request) =>
        Create(request, DynamicErrorKind.Other, StatusCodes.Status409Conflict, "The entity was changed by another request in the meantime.");

    /// <summary>
    /// The definition no longer fits the allowlist (it was tightened after the definition was saved) – never run it. The reason is
    /// for the logs; clients only learn that the endpoint is misconfigured.
    /// </summary>
    public static IResult Misconfigured(DynamicRequest request, string reason)
    {
        request.Services.GetService<ILoggerFactory>()?.CreateLogger("DynamicEndpoints.EntityFrameworkCore.Crud")
            .LogWarning("ef-crud endpoint {Method} {Route} was not run: {Reason}", request.Endpoint.Method, request.Endpoint.Route, reason);
        return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The configuration of this endpoint is not allowed.");
    }

    private static IResult Create(DynamicRequest request, DynamicErrorKind kind, int status, string title, IReadOnlyList<DynamicValidationError>? errors = null)
    {
        var factory = request.Services.GetService<IDynamicErrorResponseFactory>() ?? new DefaultDynamicErrorResponseFactory();
        return factory.CreateResponse(new DynamicErrorContext(request.HttpContext, kind, status, title, null, errors ?? [], null));
    }

    private static string Title(DynamicRequest request, string key) =>
        request.Services.GetRequiredService<IOptions<DynamicEndpointsOptions>>().Value.Messages.Format(ErrorMessage.Of(key, string.Empty));
}
