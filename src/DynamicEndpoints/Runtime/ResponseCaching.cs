using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace DynamicEndpoints.Runtime;

/// <summary><c>Cache-Control</c>, ETags and output caching of endpoints with <see cref="DynamicEndpointDefinition.Caching"/>.</summary>
internal static partial class ResponseCaching
{
    private const int MaxSeconds = 365 * 24 * 60 * 60;

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderNameRegex();

    /// <summary>Output cache tag of an endpoint – evicted whenever its definition changes.</summary>
    public static string Tag(Guid id) => $"dynamic-endpoint:{id}";

    public static void Validate(DynamicEndpointDefinition d, IServiceProvider services, ValidationErrors errors)
    {
        if (d.Caching is not { } c)
        {
            return;
        }

        if (c.NoStore)
        {
            if (c.MaxAgeSeconds is not null || c.Visibility is not null || c.ETag || c.UsesOutputCache || c.VaryByQuery is not null || c.VaryByHeader is not null)
            {
                errors.Add("caching.noStore", "'noStore' can't be combined with other caching settings.");
            }

            return;
        }

        if (d.Method != "GET")
        {
            errors.Add("caching", "Only GET endpoints can be cached (use 'noStore' to forbid caching of other endpoints).");
        }

        if (c.MaxAgeSeconds is < 0 or > MaxSeconds)
        {
            errors.Add("caching.maxAgeSeconds", $"Max age must be between 0 and {MaxSeconds} seconds.");
        }

        if (c.OutputCacheSeconds is < 1 or > MaxSeconds)
        {
            errors.Add("caching.outputCacheSeconds", $"Output cache duration must be between 1 and {MaxSeconds} seconds.");
        }

        if (c.Visibility == CacheVisibility.Public && RequiresAuthorization(d))
        {
            errors.Add("caching.visibility", "Responses of endpoints that require authorization can't be cached publicly.");
        }

        if (c.UsesOutputCache && services.GetService<IOutputCacheStore>() is null)
        {
            errors.Add("caching.outputCacheSeconds", "Output caching is not registered (call services.AddOutputCache() and app.UseOutputCache()).");
        }

        if (c.VaryByQuery is not null && !c.UsesOutputCache)
        {
            errors.Add("caching.varyByQuery", "'varyByQuery' only applies to output caching.");
        }

        foreach (var header in c.VaryByHeader ?? [])
        {
            if (!HeaderNameRegex().IsMatch(header))
            {
                errors.Add("caching.varyByHeader", $"'{header}' is not a valid header name.");
            }
        }

        if (c.MaxAgeSeconds is null && !c.ETag && !c.UsesOutputCache && c.Visibility is null)
        {
            errors.Add("caching", "Set 'maxAgeSeconds', 'eTag', 'outputCacheSeconds', 'outputCachePolicy' or 'noStore'.");
        }
    }

    /// <summary>The <c>Cache-Control</c> value, or <c>null</c> when the definition doesn't ask for one.</summary>
    public static string? CacheControl(DynamicEndpointDefinition d)
    {
        if (d.Caching is not { } c)
        {
            return null;
        }

        if (c.NoStore)
        {
            return "no-store";
        }

        if (c.MaxAgeSeconds is null && !c.ETag && c.Visibility is null)
        {
            return null;
        }

        var visibility = c.Visibility ?? (RequiresAuthorization(d) ? CacheVisibility.Private : CacheVisibility.Public);
        var directives = new List<string> { visibility == CacheVisibility.Public ? "public" : "private" };
        if (c.MaxAgeSeconds is { } maxAge)
        {
            directives.Add($"max-age={maxAge}");
        }
        else if (c.ETag)
        {
            directives.Add("no-cache");
        }

        return string.Join(", ", directives);
    }

    /// <summary>Request headers the response depends on: the endpoint's header parameters and <see cref="DynamicEndpointCaching.VaryByHeader"/>.</summary>
    public static string[] VaryHeaders(DynamicEndpointDefinition d) =>
        d.Parameters.Where(p => p.Source == ParameterSource.Header).Select(p => p.EffectiveSourceName)
            .Concat(d.Caching?.VaryByHeader ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static void AddMetadata(EndpointBuilder builder, DynamicEndpointDefinition d)
    {
        if (d.Caching is not { UsesOutputCache: true, NoStore: false } c)
        {
            return;
        }

        var tags = new[] { Tag(d.Id) };
        var headers = VaryHeaders(d);
        var query = c.VaryByQuery is { Count: > 0 } keys ? keys.ToArray() : null;
        builder.Metadata.Add(c.OutputCacheSeconds is { } seconds
            ? new OutputCacheAttribute
            {
                Duration = seconds,
                PolicyName = c.OutputCachePolicy,
                Tags = tags,
                VaryByHeaderNames = headers,
                VaryByQueryKeys = query,
            }
            : new OutputCacheAttribute
            {
                PolicyName = c.OutputCachePolicy,
                Tags = tags,
                VaryByHeaderNames = headers,
                VaryByQueryKeys = query,
            });
    }

    /// <summary>Executes the processor result with caching headers and, for ETags, a buffered body.</summary>
    public static async Task ExecuteAsync(HttpContext context, CompiledEndpoint endpoint, IResult result)
    {
        var d = endpoint.Definition;
        if (d.Caching is not { } caching)
        {
            await result.ExecuteAsync(context);
            return;
        }

        var response = context.Response;
        response.OnStarting(static state =>
        {
            var (response, endpoint) = ((HttpResponse, CompiledEndpoint))state;
            if (response.StatusCode is (>= 200 and < 300) or StatusCodes.Status304NotModified)
            {
                if (endpoint.CacheControl is { } cacheControl && !response.Headers.ContainsKey(HeaderNames.CacheControl))
                {
                    response.Headers.CacheControl = cacheControl;
                }

                if (endpoint.VaryHeaders.Length > 0)
                {
                    response.Headers.Append(HeaderNames.Vary, string.Join(", ", endpoint.VaryHeaders));
                }
            }

            return Task.CompletedTask;
        }, (response, endpoint));

        if (!caching.ETag || !HttpMethods.IsGet(context.Request.Method))
        {
            await result.ExecuteAsync(context);
            return;
        }

        var original = response.Body;
        using var buffer = new MemoryStream();
        response.Body = buffer;
        try
        {
            await result.ExecuteAsync(context);
        }
        finally
        {
            response.Body = original;
        }

        if (response.StatusCode == StatusCodes.Status200OK && !response.Headers.ContainsKey(HeaderNames.ETag))
        {
            var hash = SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            var etag = new EntityTagHeaderValue($"\"{Convert.ToHexStringLower(hash.AsSpan(0, 16))}\"");
            response.Headers.ETag = etag.ToString();

            var ifNoneMatch = context.Request.GetTypedHeaders().IfNoneMatch;
            if (ifNoneMatch.Any(tag => tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(etag, useStrongComparison: false)))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                response.ContentLength = null;
                response.Headers.Remove(HeaderNames.ContentType);
                return;
            }
        }

        if (buffer.Length > 0)
        {
            response.ContentLength ??= buffer.Length;
            buffer.Position = 0;
            await buffer.CopyToAsync(original, context.RequestAborted);
        }
    }

    private static bool RequiresAuthorization(DynamicEndpointDefinition d) =>
        !d.AllowAnonymous && (d.RequireAuthorization || d.AuthorizationPolicy is not null);
}

/// <summary>Evicts output-cached responses of endpoints that were changed or deleted, on every instance.</summary>
internal sealed class OutputCacheEvictionHandler(IServiceProvider services) : IDynamicEndpointChangeHandler
{
    public async Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken cancellationToken)
    {
        if (change.Kind == DynamicEndpointChangeKind.Created || services.GetService<IOutputCacheStore>() is not { } store)
        {
            return;
        }

        if (change.Previous?.Caching?.UsesOutputCache == true || change.Definition?.Caching?.UsesOutputCache == true)
        {
            await store.EvictByTagAsync(ResponseCaching.Tag(change.Id), cancellationToken);
        }
    }
}
