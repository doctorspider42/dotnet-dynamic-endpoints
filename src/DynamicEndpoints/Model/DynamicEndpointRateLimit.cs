using System.Text.Json.Serialization;

namespace DynamicEndpoints;

/// <summary>Algorithm of a <see cref="DynamicEndpointRateLimit"/> – the limiters of <c>System.Threading.RateLimiting</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RateLimitAlgorithm>))]
public enum RateLimitAlgorithm
{
    /// <summary><see cref="DynamicEndpointRateLimit.PermitLimit"/> requests per window of <see cref="DynamicEndpointRateLimit.WindowSeconds"/>.</summary>
    FixedWindow,
    /// <summary>Like a fixed window, but the window slides in <see cref="DynamicEndpointRateLimit.SegmentsPerWindow"/> steps – no bursts at window edges.</summary>
    SlidingWindow,
    /// <summary>
    /// A bucket of <see cref="DynamicEndpointRateLimit.PermitLimit"/> tokens, refilled with <see cref="DynamicEndpointRateLimit.TokensPerPeriod"/>
    /// tokens every <see cref="DynamicEndpointRateLimit.WindowSeconds"/> – allows short bursts.
    /// </summary>
    TokenBucket,
    /// <summary>At most <see cref="DynamicEndpointRateLimit.PermitLimit"/> requests at the same time.</summary>
    Concurrency,
}

/// <summary>Whose requests share one budget.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RateLimitPartitionKind>))]
public enum RateLimitPartitionKind
{
    /// <summary>Each client IP address has its own budget (behind a proxy, use <c>UseForwardedHeaders</c>).</summary>
    IpAddress,
    /// <summary>Each authenticated user (name identifier or name) has its own budget; anonymous requests are partitioned by IP address.</summary>
    User,
    /// <summary>Each value of <see cref="DynamicEndpointRateLimit.PartitionHeader"/> (e.g. an API key) has its own budget; requests without it by IP address.</summary>
    Header,
    /// <summary>All clients share one budget for the endpoint.</summary>
    Endpoint,
}

/// <summary>Period of a <see cref="DynamicEndpointQuota"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QuotaPeriod>))]
public enum QuotaPeriod
{
    Hour,
    Day,
    Week,
    /// <summary>30 days.</summary>
    Month,
}

/// <summary>A long-term limit on top of the rate limit, e.g. 10 000 requests per day per API key.</summary>
public sealed record DynamicEndpointQuota
{
    public int Limit { get; init; }

    public QuotaPeriod Period { get; init; } = QuotaPeriod.Day;
}

/// <summary>
/// Rate limiting defined in the endpoint itself – no named policy needed. Built on ASP.NET Core rate limiting, so the application
/// needs <c>services.AddRateLimiter()</c> and <c>app.UseRateLimiter()</c> (after <c>UseRouting</c> when that is called explicitly).
/// Rejected requests get <c>429 Too Many Requests</c> with <c>Retry-After</c>. Counters live in the memory of each instance and
/// start over when the limit settings of the definition change.
/// </summary>
public sealed record DynamicEndpointRateLimit
{
    public RateLimitAlgorithm Algorithm { get; init; } = RateLimitAlgorithm.FixedWindow;

    /// <summary>Requests per window, size of the token bucket, or concurrent requests.</summary>
    public int PermitLimit { get; init; }

    /// <summary>Window length (fixed and sliding window) or replenishment period (token bucket), in seconds. Default 60.</summary>
    public int WindowSeconds { get; init; } = 60;

    /// <summary>Segments of a sliding window. Default 6.</summary>
    public int? SegmentsPerWindow { get; init; }

    /// <summary>Tokens added every period (token bucket). Default <see cref="PermitLimit"/>.</summary>
    public int? TokensPerPeriod { get; init; }

    /// <summary>Requests that wait for a permit instead of being rejected right away. Default 0.</summary>
    public int QueueLimit { get; init; }

    public RateLimitPartitionKind PartitionBy { get; init; } = RateLimitPartitionKind.IpAddress;

    /// <summary>Header whose value identifies the client when <see cref="PartitionBy"/> is <see cref="RateLimitPartitionKind.Header"/>, e.g. <c>X-Api-Key</c>.</summary>
    public string? PartitionHeader { get; init; }

    /// <summary>Optional long-term quota for the same partitions.</summary>
    public DynamicEndpointQuota? Quota { get; init; }
}
