using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DynamicEndpoints.Processing.BuiltIn;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints;

/// <summary>Configuration of the <c>webhook</c> processor.</summary>
public sealed class WebhookConfig
{
    /// <summary>Webhook URL; placeholders (<c>{tenantId}</c>) are URL-encoded parameter values. Scheme, host and port must be literal.</summary>
    public string? Url { get; set; }

    public string Method { get; set; } = "POST";

    /// <summary>Extra headers; values may contain <c>{name}</c> placeholders and <c>{config:Key}</c> references for secrets.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>JSON template of the payload (<c>{{name}}</c> placeholders). Default: all validated parameters.</summary>
    public JsonNode? Payload { get; set; }

    /// <summary>Retries after the first attempt, for network errors, timeouts, 408, 429 and 5xx responses. Default 3.</summary>
    [Range(0, 10)]
    public int Retries { get; set; } = 3;

    /// <summary>Delay before the first retry, doubled for every further one (a <c>Retry-After</c> of the receiver wins). Default 500 ms.</summary>
    [Range(0, 60_000)]
    public int RetryDelayMilliseconds { get; set; } = 500;

    /// <summary>Timeout of a single attempt.</summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Configuration key of a secret (<c>Webhooks:Secret</c>). When set, the payload is signed with HMAC-SHA256 and the signature is sent
    /// as <c>sha256=&lt;hex&gt;</c> in <see cref="SignatureHeader"/>. The secret itself never lives in the definition.
    /// </summary>
    public string? SigningSecretConfigurationKey { get; set; }

    public string SignatureHeader { get; set; } = "X-Webhook-Signature";

    /// <summary>
    /// Answer right away and deliver in the background (in memory – deliveries still pending at shutdown are lost). By default the
    /// response waits for the delivery: <see cref="StatusCode"/> on success, <c>502</c> once all attempts failed.
    /// </summary>
    public bool Background { get; set; }

    /// <summary>Status code returned on success (or when queued). Default 202.</summary>
    [Range(200, 299)]
    public int StatusCode { get; set; } = StatusCodes.Status202Accepted;
}

/// <summary>
/// Sends the validated request as a JSON webhook, with retries, exponential back-off, an optional HMAC signature and a delivery id
/// (<c>X-Webhook-Delivery</c>, the same for all attempts – receivers can deduplicate). Register with <c>AddWebhookProcessor()</c>.
/// </summary>
[DynamicProcessor("webhook",
    Description = "Delivers the request as a JSON webhook with retries.",
    ConfigurationExample = """{ "url": "https://hooks.example.com/orders", "retries": 3, "signingSecretConfigurationKey": "Webhooks:Secret" }""")]
public sealed class WebhookProcessor(
    WebhookSender sender,
    IOptions<DynamicHttpProcessorOptions> options,
    IConfiguration configuration) : DynamicEndpointProcessor<WebhookConfig>
{
    protected override IEnumerable<string> Validate(WebhookConfig config)
    {
        foreach (var error in HttpProcessorChecks.Check(config.Url, config.Method, config.Headers, options.Value, configuration))
        {
            yield return error;
        }

        if (config.SigningSecretConfigurationKey is { } key && string.IsNullOrEmpty(configuration[key]))
        {
            yield return $"The signing secret '{key}' is not set in the configuration.";
        }
    }

    protected override async Task<IResult> ProcessAsync(DynamicRequest request, WebhookConfig config)
    {
        var delivery = sender.Prepare(request, config);
        if (config.Background)
        {
            await sender.EnqueueAsync(delivery);
            return Results.Json(new { deliveryId = delivery.Id, queued = true }, statusCode: config.StatusCode);
        }

        var outcome = await sender.DeliverAsync(delivery, request.RequestAborted);
        return outcome.Delivered
            ? Results.Json(new { deliveryId = delivery.Id, delivered = true, attempts = outcome.Attempts, status = outcome.StatusCode }, statusCode: config.StatusCode)
            : Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Webhook delivery failed",
                detail: $"No successful response after {outcome.Attempts} attempt(s).",
                extensions: new Dictionary<string, object?> { ["deliveryId"] = delivery.Id });
    }
}

/// <summary>A prepared webhook delivery – rendered once, sent identically on every attempt.</summary>
public sealed record WebhookDelivery(string Id, Guid EndpointId, string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Payload, WebhookConfig Config);

public sealed record WebhookOutcome(bool Delivered, int Attempts, int? StatusCode);

/// <summary>Sends webhook deliveries with retries; background deliveries go through an in-memory queue.</summary>
public sealed class WebhookSender(
    IHttpClientFactory httpClientFactory,
    IOptions<DynamicHttpProcessorOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<WebhookSender> logger)
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly Channel<WebhookDelivery> _queue = Channel.CreateUnbounded<WebhookDelivery>(new UnboundedChannelOptions { SingleReader = true });

    internal ChannelReader<WebhookDelivery> Queue => _queue.Reader;

    public WebhookDelivery Prepare(DynamicRequest request, WebhookConfig config)
    {
        var payload = config.Payload is null ? request.Parameters.DeepClone() : DynamicTemplate.RenderJson(config.Payload, request.Parameters);
        var body = Encoding.UTF8.GetBytes(payload?.ToJsonString() ?? "null");
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, template) in config.Headers ?? [])
        {
            headers.Add(KeyValuePair.Create(name, DynamicTemplate.RenderHeader(template, request.Parameters, configuration)));
        }

        if (config.SigningSecretConfigurationKey is { } key && configuration[key] is { Length: > 0 } secret)
        {
            var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
            headers.Add(KeyValuePair.Create(config.SignatureHeader, "sha256=" + Convert.ToHexStringLower(signature)));
        }

        var id = Guid.CreateVersion7().ToString("N");
        headers.Add(KeyValuePair.Create("X-Webhook-Delivery", id));
        return new WebhookDelivery(id, request.Endpoint.Id, config.Method, DynamicTemplate.RenderUrl(config.Url!, request.Parameters), headers, body, config);
    }

    public ValueTask EnqueueAsync(WebhookDelivery delivery) => _queue.Writer.WriteAsync(delivery);

    public async Task<WebhookOutcome> DeliverAsync(WebhookDelivery delivery, CancellationToken cancellationToken)
    {
        if (options.Value.CheckHost(delivery.Url) is { } hostError)
        {
            logger.LogWarning("Webhook {Delivery} of dynamic endpoint {Id} was not sent: {Error}", delivery.Id, delivery.EndpointId, hostError);
            return new WebhookOutcome(false, 0, null);
        }

        var client = httpClientFactory.CreateClient(options.Value.HttpClientName);
        var attempts = 0;
        int? status = null;
        while (true)
        {
            attempts++;
            TimeSpan? retryAfter = null;
            try
            {
                using var message = new HttpRequestMessage(new HttpMethod(delivery.Method), delivery.Url)
                {
                    Content = new ByteArrayContent(delivery.Payload) { Headers = { ContentType = new("application/json") { CharSet = "utf-8" } } },
                };
                foreach (var (name, value) in delivery.Headers)
                {
                    message.Headers.TryAddWithoutValidation(name, value);
                }

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(delivery.Config.TimeoutSeconds));
                using var response = await client.SendAsync(message, timeout.Token);
                status = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    logger.LogDebug("Webhook {Delivery} delivered after {Attempts} attempt(s).", delivery.Id, attempts);
                    return new WebhookOutcome(true, attempts, status);
                }

                if (!IsTransient(response.StatusCode))
                {
                    logger.LogWarning("Webhook {Delivery} of dynamic endpoint {Id} was rejected with {Status}.", delivery.Id, delivery.EndpointId, status);
                    return new WebhookOutcome(false, attempts, status);
                }

                retryAfter = response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date is { } date ? date - timeProvider.GetUtcNow() : null);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogDebug(ex, "Webhook {Delivery} attempt {Attempt} failed.", delivery.Id, attempts);
            }

            if (attempts > delivery.Config.Retries)
            {
                logger.LogWarning("Webhook {Delivery} of dynamic endpoint {Id} failed after {Attempts} attempt(s).", delivery.Id, delivery.EndpointId, attempts);
                return new WebhookOutcome(false, attempts, status);
            }

            var backoff = TimeSpan.FromMilliseconds(delivery.Config.RetryDelayMilliseconds * Math.Pow(2, attempts - 1));
            var delay = retryAfter is { } wait && wait > backoff ? wait : backoff;
            await Task.Delay(delay > MaxDelay ? MaxDelay : delay, timeProvider, cancellationToken);
        }
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}

/// <summary>Delivers background webhooks one after another.</summary>
internal sealed class WebhookBackgroundService(WebhookSender sender, ILogger<WebhookBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in sender.Queue.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await sender.DeliverAsync(delivery, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Webhook {Delivery} failed unexpectedly.", delivery.Id);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        var pending = sender.Queue.Count;
        if (pending > 0)
        {
            logger.LogWarning("{Count} webhook deliveries were still queued at shutdown and are lost.", pending);
        }
    }
}
