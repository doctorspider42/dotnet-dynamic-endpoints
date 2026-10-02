using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using DynamicEndpoints.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Runtime;

/// <summary>Rate limits defined in <see cref="DynamicEndpointDefinition.RateLimit"/>, enforced by ASP.NET Core rate limiting.</summary>
internal static partial class RateLimiting
{
    /// <summary>The rate limiting policy that serves all definition-defined limits; registered by <c>AddDynamicEndpoints</c>.</summary>
    public const string PolicyName = "DynamicEndpoints.RateLimit";

    private const int MaxWindowSeconds = 24 * 60 * 60;

    [GeneratedRegex("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$")]
    private static partial Regex HeaderNameRegex();

    public static void Validate(DynamicEndpointDefinition d, ValidationErrors errors)
    {
        if (d.RateLimit is not { } r)
        {
            return;
        }

        if (d.RateLimitingPolicy is not null)
        {
            errors.Add("rateLimit", "Use either a named 'rateLimitingPolicy' or a 'rateLimit' of the endpoint, not both.");
        }

        if (r.PermitLimit < 1)
        {
            errors.Add("rateLimit.permitLimit", "The permit limit must be at least 1.");
        }

        if (r.Algorithm != RateLimitAlgorithm.Concurrency && r.WindowSeconds is < 1 or > MaxWindowSeconds)
        {
            errors.Add("rateLimit.windowSeconds", $"The window must be between 1 and {MaxWindowSeconds} seconds (use 'quota' for longer periods).");
        }

        if (r.SegmentsPerWindow is not null && r.Algorithm != RateLimitAlgorithm.SlidingWindow)
        {
            errors.Add("rateLimit.segmentsPerWindow", "Segments can only be set for the SlidingWindow algorithm.");
        }
        else if (r.SegmentsPerWindow is < 1 or > 1000)
        {
            errors.Add("rateLimit.segmentsPerWindow", "Segments per window must be between 1 and 1000.");
        }

        if (r.TokensPerPeriod is not null && r.Algorithm != RateLimitAlgorithm.TokenBucket)
        {
            errors.Add("rateLimit.tokensPerPeriod", "Tokens per period can only be set for the TokenBucket algorithm.");
        }
        else if (r.TokensPerPeriod < 1)
        {
            errors.Add("rateLimit.tokensPerPeriod", "Tokens per period must be at least 1.");
        }

        if (r.QueueLimit is < 0 or > 10_000)
        {
            errors.Add("rateLimit.queueLimit", "The queue limit must be between 0 and 10000.");
        }

        if (r.PartitionBy == RateLimitPartitionKind.Header)
        {
            if (r.PartitionHeader is null || !HeaderNameRegex().IsMatch(r.PartitionHeader))
            {
                errors.Add("rateLimit.partitionHeader", "Partitioning by header needs a valid 'partitionHeader', e.g. 'X-Api-Key'.");
            }
        }
        else if (r.PartitionHeader is not null)
        {
            errors.Add("rateLimit.partitionHeader", "'partitionHeader' can only be set when partitioning by Header.");
        }

        if (r.Quota is { Limit: < 1 })
        {
            errors.Add("rateLimit.quota.limit", "The quota limit must be at least 1.");
        }
    }

    public static void AddMetadata(EndpointBuilder builder, DynamicEndpointDefinition d)
    {
        if (d.RateLimit is not { } limit)
        {
            return;
        }

        // The settings are part of the partition key: changed limits start with fresh counters, unchanged ones (e.g. after a reload)
        // keep theirs. Limiters of old settings are idle and cleaned up by the rate limiting middleware.
        var fingerprint = $"{d.Id:N}:{JsonSerializer.Serialize(limit, DynamicEndpointsJson.SerializerOptions)}";
        builder.Metadata.Add(new DynamicRateLimitMetadata(limit, fingerprint));
        builder.Metadata.Add(new EnableRateLimitingAttribute(PolicyName));
    }

    public static TimeSpan Length(QuotaPeriod period) => period switch
    {
        QuotaPeriod.Hour => TimeSpan.FromHours(1),
        QuotaPeriod.Day => TimeSpan.FromDays(1),
        QuotaPeriod.Week => TimeSpan.FromDays(7),
        _ => TimeSpan.FromDays(30),
    };

    /// <summary>Human-readable summary, e.g. "10 requests per 60 s per IP address; quota 1000 per day".</summary>
    public static string Describe(DynamicEndpointRateLimit r)
    {
        var partition = r.PartitionBy switch
        {
            RateLimitPartitionKind.IpAddress => "per IP address",
            RateLimitPartitionKind.User => "per user",
            RateLimitPartitionKind.Header => $"per `{r.PartitionHeader}`",
            _ => "for all clients together",
        };
        var limit = r.Algorithm switch
        {
            RateLimitAlgorithm.Concurrency => $"{r.PermitLimit} concurrent requests",
            RateLimitAlgorithm.TokenBucket => $"bursts of {r.PermitLimit} requests, {r.TokensPerPeriod ?? r.PermitLimit} more every {r.WindowSeconds} s",
            _ => $"{r.PermitLimit} requests per {r.WindowSeconds} s",
        };
        var text = $"{limit} {partition}";
        return r.Quota is { } quota ? $"{text}; quota {quota.Limit} requests per {quota.Period.ToString().ToLowerInvariant()}" : text;
    }
}

/// <summary>Rate limit of a routed dynamic endpoint, read by <see cref="DynamicRateLimiterPolicy"/>.</summary>
internal sealed class DynamicRateLimitMetadata(DynamicEndpointRateLimit limit, string fingerprint)
{
    public DynamicEndpointRateLimit Limit { get; } = limit;

    public string Fingerprint { get; } = fingerprint;

    public RateLimiter CreateLimiter()
    {
        var r = Limit;
        var window = TimeSpan.FromSeconds(r.WindowSeconds);
        RateLimiter limiter = r.Algorithm switch
        {
            RateLimitAlgorithm.SlidingWindow => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = r.PermitLimit,
                Window = window,
                SegmentsPerWindow = r.SegmentsPerWindow ?? 6,
                QueueLimit = r.QueueLimit,
            }),
            RateLimitAlgorithm.TokenBucket => new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
            {
                TokenLimit = r.PermitLimit,
                TokensPerPeriod = r.TokensPerPeriod ?? r.PermitLimit,
                ReplenishmentPeriod = window,
                QueueLimit = r.QueueLimit,
            }),
            RateLimitAlgorithm.Concurrency => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = r.PermitLimit,
                QueueLimit = r.QueueLimit,
            }),
            _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = r.PermitLimit,
                Window = window,
                QueueLimit = r.QueueLimit,
            }),
        };

        if (r.Quota is not { } quota)
        {
            return limiter;
        }

        var quotaLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = quota.Limit,
            Window = RateLimiting.Length(quota.Period),
            QueueLimit = 0,
        });
        return RateLimiter.CreateChained(limiter, quotaLimiter);
    }
}

internal readonly record struct DynamicRateLimitKey(string Endpoint, string Client);

internal sealed class ConfigureDynamicRateLimiting : IConfigureOptions<RateLimiterOptions>
{
    public void Configure(RateLimiterOptions options) =>
        options.AddPolicy(RateLimiting.PolicyName, new DynamicRateLimiterPolicy());
}

/// <summary>Partitions requests by endpoint settings and client, and answers rejected requests with 429 through the error factory.</summary>
internal sealed class DynamicRateLimiterPolicy : IRateLimiterPolicy<DynamicRateLimitKey>
{
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = static async (context, _) =>
    {
        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            http.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var services = http.RequestServices;
        var messages = services.GetRequiredService<IOptions<DynamicEndpointsOptions>>().Value.Messages;
        var factory = services.GetService<IDynamicErrorResponseFactory>() ?? new DefaultDynamicErrorResponseFactory();
        var title = messages.Format(ErrorMessage.Of("title.tooManyRequests", string.Empty));
        await factory.CreateResponse(new DynamicErrorContext(
            http, DynamicErrorKind.TooManyRequests, StatusCodes.Status429TooManyRequests, title, null, [], null)).ExecuteAsync(http);
    };

    public RateLimitPartition<DynamicRateLimitKey> GetPartition(HttpContext httpContext)
    {
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<DynamicRateLimitMetadata>() is not { } metadata)
        {
            return RateLimitPartition.GetNoLimiter(default(DynamicRateLimitKey));
        }

        var key = new DynamicRateLimitKey(metadata.Fingerprint, Client(httpContext, metadata.Limit));
        return RateLimitPartition.Get(key, _ => metadata.CreateLimiter());
    }

    private static string Client(HttpContext context, DynamicEndpointRateLimit limit)
    {
        string Ip() => "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        switch (limit.PartitionBy)
        {
            case RateLimitPartitionKind.Endpoint:
                return string.Empty;
            case RateLimitPartitionKind.User when context.User.Identity?.IsAuthenticated == true:
                var user = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.Identity.Name;
                return user is null ? Ip() : "user:" + user;
            case RateLimitPartitionKind.Header when limit.PartitionHeader is { } header:
                var value = context.Request.Headers[header].ToString();
                return value.Length == 0 ? Ip() : "header:" + value;
            default:
                return Ip();
        }
    }
}
