using System.Text.Json.Serialization;

namespace DynamicEndpoints;

/// <summary>Who may store a cached response – the <c>public</c> / <c>private</c> directive of <c>Cache-Control</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CacheVisibility>))]
public enum CacheVisibility
{
    /// <summary>Browsers and shared caches (CDNs, proxies).</summary>
    Public,
    /// <summary>Only the client's own cache – the default for endpoints that require authorization.</summary>
    Private,
}

/// <summary>
/// Response caching of an endpoint: <c>Cache-Control</c> headers, ETags with <c>304 Not Modified</c> and server-side output caching
/// (ASP.NET Core output caching – <c>services.AddOutputCache()</c> and <c>app.UseOutputCache()</c>). Apart from
/// <see cref="NoStore"/>, only GET endpoints can be cached. Headers are only added to successful responses.
/// </summary>
public sealed record DynamicEndpointCaching
{
    /// <summary>
    /// How long clients (and, when <see cref="CacheVisibility.Public"/>, shared caches) may reuse a response, in seconds:
    /// <c>Cache-Control: max-age</c>.
    /// </summary>
    public int? MaxAgeSeconds { get; init; }

    /// <summary>
    /// <c>public</c> or <c>private</c>. By default <see cref="CacheVisibility.Private"/> for endpoints that require authorization,
    /// otherwise <see cref="CacheVisibility.Public"/>. Responses that require authorization can't be public.
    /// </summary>
    public CacheVisibility? Visibility { get; init; }

    /// <summary><c>Cache-Control: no-store</c> – responses must not be cached at all. Can't be combined with the other settings.</summary>
    public bool NoStore { get; init; }

    /// <summary>
    /// Adds an <c>ETag</c> computed from the response body and answers matching <c>If-None-Match</c> requests with
    /// <c>304 Not Modified</c>. Without <see cref="MaxAgeSeconds"/> clients are told to revalidate every time (<c>no-cache</c>).
    /// </summary>
    public bool ETag { get; init; }

    /// <summary>
    /// Caches successful responses on the server for this many seconds with ASP.NET Core output caching. The cache key contains the
    /// path, the query string and the endpoint's header parameters; entries are evicted whenever the definition changes.
    /// Requests with an <c>Authorization</c> header or cookies are not cached (the default output caching policy).
    /// </summary>
    public int? OutputCacheSeconds { get; init; }

    /// <summary>Name of an output cache policy registered with <c>AddOutputCache(o => o.AddPolicy(…))</c>, applied on top.</summary>
    public string? OutputCachePolicy { get; init; }

    /// <summary>Query keys the output cache varies by. Default: the whole query string.</summary>
    public IReadOnlyList<string>? VaryByQuery { get; init; }

    /// <summary>
    /// Further request headers the response depends on: part of the output cache key, and listed in the <c>Vary</c> response header.
    /// The endpoint's own header parameters are always included.
    /// </summary>
    public IReadOnlyList<string>? VaryByHeader { get; init; }

    [JsonIgnore]
    internal bool UsesOutputCache => OutputCacheSeconds is not null || OutputCachePolicy is not null;
}
