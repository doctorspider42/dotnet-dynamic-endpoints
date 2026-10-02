using DynamicEndpoints;
using DynamicEndpoints.AdminUI;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Microsoft.AspNetCore.Builder;

public static class DynamicEndpointsAdminUIEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Serves the admin panel under <paramref name="pattern"/> (e.g. <c>/admin/</c>) and reserves the prefix. It talks to the
    /// admin REST API mapped with <c>MapDynamicEndpointsAdmin(adminApiPath)</c>. Secure both the same way, e.g.
    /// <c>.RequireAuthorization("admin")</c> on the returned group – the panel itself holds no data, the API does.
    /// </summary>
    /// <param name="endpoints">The application.</param>
    /// <param name="pattern">Path of the panel.</param>
    /// <param name="adminApiPath">Prefix passed to <c>MapDynamicEndpointsAdmin</c> (relative to the path base), or an absolute URL.</param>
    /// <param name="configure">Title and links of the panel.</param>
    public static RouteGroupBuilder MapDynamicEndpointsAdminUI(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/admin",
        string adminApiPath = "/_dynamic-endpoints",
        Action<DynamicEndpointsAdminUIOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(adminApiPath);

        var options = new DynamicEndpointsAdminUIOptions();
        configure?.Invoke(options);
        var root = "/" + pattern.Trim('/');
        endpoints.ServiceProvider.GetService<RouteInspector>()?.Reserve(root);

        var group = endpoints.MapGroup(root).ExcludeFromDescription();
        IResult Index(HttpContext context)
        {
            SetSecurityHeaders(context.Response);
            context.Response.Headers.CacheControl = "no-cache";
            var html = AdminUIAssets.RenderIndex(context.Request.PathBase.Value ?? string.Empty, root, adminApiPath, options);
            return Results.Content(html, "text/html; charset=utf-8");
        }

        group.MapGet("/", Index);
        group.MapGet("/{file}", (string file, HttpContext context) =>
        {
            if (file.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                return Index(context);
            }

            if (!AdminUIAssets.TryGet(file, out var asset))
            {
                return Results.NotFound();
            }

            SetSecurityHeaders(context.Response);
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.Headers.ETag = asset.ETag;
            return context.Request.Headers.IfNoneMatch.Contains(asset.ETag)
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(asset.Content, asset.ContentType);
        });

        return group;
    }

    private static void SetSecurityHeaders(HttpResponse response)
    {
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers[HeaderNames.ContentSecurityPolicy] = "frame-ancestors 'none'";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
