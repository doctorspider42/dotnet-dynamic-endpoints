using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicEndpoints.Processing.BuiltIn;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints;

/// <summary>Settings of the built-in HTTP processors (<c>http-forward</c>, <c>webhook</c>), set by the application – not by admins.</summary>
public sealed class DynamicHttpProcessorOptions
{
    /// <summary>
    /// Name of the <c>IHttpClientFactory</c> client the processors use. Configure it like any named client, e.g.
    /// <c>services.AddHttpClient("DynamicEndpoints").AddStandardResilienceHandler()</c>.
    /// </summary>
    public string HttpClientName { get; set; } = "DynamicEndpoints";

    /// <summary>
    /// Hosts the processors may call (<c>api.example.com</c>, <c>*.example.com</c>), checked when a definition is saved and on every
    /// call. Empty (default) allows any host – restrict it when the admins aren't fully trusted (SSRF).
    /// </summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Largest upstream response the forward processor relays, in bytes. Default 10 MB.</summary>
    public long MaxResponseBodySize { get; set; } = 10 * 1024 * 1024;

    internal string? CheckHost(string url)
    {
        if (AllowedHosts.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host;
        var allowed = AllowedHosts.Any(a => a.StartsWith("*.", StringComparison.Ordinal)
            ? host.EndsWith(a[1..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(a, host, StringComparison.OrdinalIgnoreCase));
        return allowed ? null : $"Host '{host}' is not allowed. Allowed: {string.Join(", ", AllowedHosts)}.";
    }
}

/// <summary>What the forward processor sends as the upstream request body.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HttpForwardBody>))]
public enum HttpForwardBody
{
    /// <summary>The <see cref="ParameterSource.Body"/> parameters as a JSON object, when the endpoint has any; otherwise nothing.</summary>
    Auto,
    /// <summary>All validated parameters as a JSON object.</summary>
    Parameters,
    /// <summary>Only the <see cref="ParameterSource.Body"/> parameters.</summary>
    BodyParameters,
    None,
}

/// <summary>Configuration of the <c>http-forward</c> processor.</summary>
public sealed class HttpForwardConfig
{
    /// <summary>
    /// Target URL, e.g. <c>https://backend.internal/orders/{id}?expand={expand}</c>. Placeholders are parameter names; their values
    /// are URL-encoded. Scheme, host and port must be literal.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>HTTP method of the upstream request. Default: the method of the dynamic endpoint.</summary>
    public string? Method { get; set; }

    /// <summary>
    /// Headers sent upstream. Values may contain parameter placeholders (<c>{tenantId}</c>) and configuration references for
    /// secrets (<c>{config:Backend:ApiKey}</c>) – keep secrets out of definitions.
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>Request headers copied from the incoming request, e.g. <c>Authorization</c> or <c>Accept-Language</c>.</summary>
    public List<string>? ForwardHeaders { get; set; }

    public HttpForwardBody Body { get; set; } = HttpForwardBody.Auto;

    /// <summary>JSON template of the upstream body (<c>{{name}}</c> placeholders) – overrides <see cref="Body"/>.</summary>
    public JsonNode? BodyTemplate { get; set; }

    [Range(1, 300)]
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Maps the upstream response instead of relaying it: a JSON template that sees the parameters, <c>{{response…}}</c> (the parsed
    /// upstream body) and <c>{{status}}</c>.
    /// </summary>
    public JsonNode? ResponseTemplate { get; set; }

    /// <summary>Status code of the response. Default: the upstream status code.</summary>
    [Range(100, 599)]
    public int? StatusCode { get; set; }
}

/// <summary>
/// Forwards validated requests to another HTTP service (a proxy with a contract): URL template, headers, timeout, and either the
/// upstream response as is or mapped by a template. Register with <c>AddHttpForwardProcessor()</c>.
/// </summary>
[DynamicProcessor("http-forward",
    Description = "Forwards the request to another HTTP service and returns its response.",
    ConfigurationExample = """{ "url": "https://backend.example.com/orders/{id}", "headers": { "X-Api-Key": "{config:Backend:ApiKey}" }, "timeoutSeconds": 30 }""")]
public sealed class HttpForwardProcessor(
    IHttpClientFactory httpClientFactory,
    IOptions<DynamicHttpProcessorOptions> options,
    IConfiguration configuration,
    ILogger<HttpForwardProcessor> logger) : DynamicEndpointProcessor<HttpForwardConfig>
{
    // Never relayed: connection-level headers and headers the server sets itself.
    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Authenticate", "Trailer", "TE", "Content-Length", "Server", "Date",
        "Set-Cookie", "Strict-Transport-Security", "Alt-Svc",
    };

    protected override IEnumerable<string> Validate(HttpForwardConfig config) =>
        HttpProcessorChecks.Check(config.Url, config.Method, config.Headers, options.Value, configuration);

    protected override async Task<IResult> ProcessAsync(DynamicRequest request, HttpForwardConfig config)
    {
        var url = DynamicTemplate.RenderUrl(config.Url!, request.Parameters);
        if (options.Value.CheckHost(url) is { } hostError)
        {
            logger.LogWarning("Dynamic endpoint {Id} forwards to a host that is not allowed: {Error}", request.Endpoint.Id, hostError);
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Upstream host not allowed");
        }

        using var message = new HttpRequestMessage(new HttpMethod(config.Method ?? request.HttpContext.Request.Method), url);
        var body = config.BodyTemplate is not null
            ? DynamicTemplate.RenderJson(config.BodyTemplate, request.Parameters)
            : BodyOf(request, config.Body);
        if (body is not null)
        {
            message.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        foreach (var name in config.ForwardHeaders ?? [])
        {
            if (request.HttpContext.Request.Headers.TryGetValue(name, out var values))
            {
                message.Headers.TryAddWithoutValidation(name, values.ToArray());
            }
        }

        foreach (var (name, template) in config.Headers ?? [])
        {
            var value = DynamicTemplate.RenderHeader(template, request.Parameters, configuration);
            message.Headers.Remove(name);
            if (!message.Headers.TryAddWithoutValidation(name, value))
            {
                message.Content?.Headers.Remove(name);
                message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(request.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
        try
        {
            var client = httpClientFactory.CreateClient(options.Value.HttpClientName);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var content = await ReadLimitedAsync(response.Content, options.Value.MaxResponseBodySize, timeout.Token);
            if (content is null)
            {
                logger.LogWarning("Upstream response of dynamic endpoint {Id} exceeds {Limit} bytes.", request.Endpoint.Id, options.Value.MaxResponseBodySize);
                return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Upstream response too large");
            }

            var status = config.StatusCode ?? (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (config.ResponseTemplate is not null)
            {
                var data = DynamicTemplate.Data(request.Parameters,
                    ("response", DynamicTemplate.ParseBody(content, contentType)),
                    ("status", JsonValue.Create((int)response.StatusCode)));
                return Results.Json(DynamicTemplate.RenderJson(config.ResponseTemplate, data), statusCode: status);
            }

            var headers = response.Headers.Concat(response.Content.Headers)
                .Where(h => !SkippedResponseHeaders.Contains(h.Key) && !string.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return new RelayedResult(status, contentType, headers, content);
        }
        catch (OperationCanceledException) when (!request.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("Upstream request of dynamic endpoint {Id} to {Url} timed out.", request.Endpoint.Id, message.RequestUri);
            return Results.Problem(statusCode: StatusCodes.Status504GatewayTimeout, title: "Upstream timeout");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request of dynamic endpoint {Id} to {Url} failed.", request.Endpoint.Id, message.RequestUri);
            return Results.Problem(statusCode: StatusCodes.Status502BadGateway, title: "Upstream request failed");
        }
    }

    private static JsonObject? BodyOf(DynamicRequest request, HttpForwardBody mode)
    {
        JsonObject BodyParameters()
        {
            var body = new JsonObject();
            foreach (var p in request.Metadata.Parameters.Where(p => p.Source == ParameterSource.Body))
            {
                if (request.Parameters.TryGetPropertyValue(p.Name, out var value))
                {
                    body[p.Name] = value?.DeepClone();
                }
            }

            return body;
        }

        return mode switch
        {
            HttpForwardBody.Parameters => (JsonObject)request.Parameters.DeepClone(),
            HttpForwardBody.BodyParameters => BodyParameters(),
            HttpForwardBody.Auto when request.Metadata.HasJsonBody => BodyParameters(),
            _ => null,
        };
    }

    private static async Task<byte[]?> ReadLimitedAsync(HttpContent content, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private sealed class RelayedResult(int status, string? contentType, List<KeyValuePair<string, IEnumerable<string>>> headers, byte[] content) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            response.StatusCode = status;
            foreach (var (name, values) in headers)
            {
                response.Headers[name] = values.ToArray();
            }

            if (contentType is not null)
            {
                response.ContentType = contentType;
            }

            if (content.Length > 0 && status != (int)HttpStatusCode.NoContent && status != (int)HttpStatusCode.NotModified)
            {
                response.ContentLength = content.Length;
                await response.Body.WriteAsync(content, httpContext.RequestAborted);
            }
        }
    }
}

internal static class HttpProcessorChecks
{
    public static IEnumerable<string> Check(
        string? url,
        string? method,
        IReadOnlyDictionary<string, string>? headers,
        DynamicHttpProcessorOptions options,
        IConfiguration configuration)
    {
        if (DynamicTemplate.CheckUrl(url, "url") is { } urlError)
        {
            yield return urlError;
        }
        else if (options.CheckHost(url!) is { } hostError)
        {
            yield return hostError;
        }

        if (method is not null && !HttpMethods.IsGet(method) && !HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) &&
            !HttpMethods.IsPatch(method) && !HttpMethods.IsDelete(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method))
        {
            yield return $"'{method}' is not a supported HTTP method.";
        }

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (!IsToken(name))
            {
                yield return $"'{name}' is not a valid header name.";
            }

            if (value.Contains('\r') || value.Contains('\n'))
            {
                yield return $"Header '{name}' can't contain line breaks.";
            }

            foreach (var key in DynamicTemplate.ConfigurationKeys(value))
            {
                if (configuration[key] is null)
                {
                    yield return $"Header '{name}' references configuration '{key}', which is not set.";
                }
            }
        }
    }

    private static bool IsToken(string name) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));
}
