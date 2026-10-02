using DynamicEndpoints;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

public sealed class DynamicErrorResponseOptions
{
    /// <summary>Requests whose errors are formatted; all by default. E.g. <c>c =&gt; c.Request.Path.StartsWithSegments("/api")</c>.</summary>
    public Func<HttpContext, bool>? AppliesTo { get; set; }

    /// <summary>
    /// Catch unhandled exceptions, log them and answer with a 500 built by the factory (<see cref="DynamicErrorKind.Exception"/>).
    /// Default <c>true</c>. Turn it off when <c>UseExceptionHandler</c> should see them.
    /// </summary>
    public bool HandleExceptions { get; set; } = true;
}

public static class DynamicErrorResponseApplicationBuilderExtensions
{
    /// <summary>
    /// Sends empty error responses – 401/403 from authentication and authorization, 404 when no route matched, 405, and empty
    /// errors returned by processors – and unhandled exceptions through <see cref="IDynamicErrorResponseFactory"/>, so every error
    /// has the same format as the validation errors of dynamic endpoints. A replacement for <c>UseStatusCodePages</c>.
    /// Add it early, before <c>UseAuthentication</c>/<c>UseAuthorization</c>. Responses that already have a body are left alone,
    /// and endpoints with <c>[SkipStatusCodePages]</c> are skipped.
    /// </summary>
    public static IApplicationBuilder UseDynamicEndpointsErrorResponses(this IApplicationBuilder app, Action<DynamicErrorResponseOptions>? configure = null)
    {
        var options = new DynamicErrorResponseOptions();
        configure?.Invoke(options);
        return app.UseMiddleware<DynamicErrorResponseMiddleware>(options);
    }
}

internal sealed class DynamicErrorResponseMiddleware(
    RequestDelegate next,
    DynamicErrorResponseOptions errorOptions,
    IOptions<DynamicEndpointsOptions> options,
    ILogger<DynamicErrorResponseMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (errorOptions.AppliesTo?.Invoke(context) == false)
        {
            await next(context);
            return;
        }

        // Same switch as UseStatusCodePages: endpoints and middleware can turn it off for a single request.
        var feature = new StatusCodePagesFeature();
        context.Features.Set<IStatusCodePagesFeature>(feature);

        try
        {
            await next(context);
        }
        catch (Exception ex) when (errorOptions.HandleExceptions && !context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogError(ex, "Unhandled exception while processing {Method} {Path}.", context.Request.Method, context.Request.Path);
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await WriteAsync(context, DynamicErrorKind.Exception, Title("title.error"), ex);
            return;
        }

        var response = context.Response;
        if (!feature.Enabled ||
            response.HasStarted ||
            response.StatusCode is < 400 or >= 600 ||
            response.ContentLength > 0 ||
            !string.IsNullOrEmpty(response.ContentType) ||
            context.GetEndpoint()?.Metadata.GetMetadata<ISkipStatusCodePagesMetadata>() is not null)
        {
            return;
        }

        var (kind, title) = response.StatusCode switch
        {
            StatusCodes.Status401Unauthorized => (DynamicErrorKind.Unauthorized, Title("title.unauthorized")),
            StatusCodes.Status403Forbidden => (DynamicErrorKind.Forbidden, Title("title.forbidden")),
            StatusCodes.Status404NotFound => (DynamicErrorKind.NotFound, Title("title.notFound")),
            StatusCodes.Status405MethodNotAllowed => (DynamicErrorKind.MethodNotAllowed, Title("title.methodNotAllowed")),
            StatusCodes.Status413PayloadTooLarge => (DynamicErrorKind.PayloadTooLarge, Title("title.payloadTooLarge")),
            StatusCodes.Status415UnsupportedMediaType => (DynamicErrorKind.UnsupportedMediaType, Title("title.unsupportedMediaType")),
            StatusCodes.Status500InternalServerError => (DynamicErrorKind.Exception, Title("title.error")),
            var status => (DynamicErrorKind.Other, Title("title.status", status)),
        };
        await WriteAsync(context, kind, title, null);
    }

    private string Title(string key, params object?[] arguments) =>
        options.Value.Messages.Format(ErrorMessage.Of(key, string.Empty, arguments));

    private static async Task WriteAsync(HttpContext context, DynamicErrorKind kind, string title, Exception? exception)
    {
        var factory = context.RequestServices.GetService<IDynamicErrorResponseFactory>() ?? new DefaultDynamicErrorResponseFactory();
        var status = context.Response.StatusCode;
        var result = factory.CreateResponse(new DynamicErrorContext(context, kind, status, title, null, [], exception));
        await result.ExecuteAsync(context);
    }
}
