using DynamicEndpoints;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Opt-in registration of the built-in processors.</summary>
public static class DynamicEndpointsBuiltInProcessorsExtensions
{
    /// <summary>Registers <c>http-forward</c>, <c>webhook</c> and <c>response</c>.</summary>
    public static IDynamicEndpointsBuilder AddBuiltInProcessors(this IDynamicEndpointsBuilder builder, Action<DynamicHttpProcessorOptions>? configure = null) =>
        builder.AddHttpForwardProcessor(configure: configure).AddWebhookProcessor().AddResponseTemplateProcessor();

    /// <summary>
    /// Registers <see cref="HttpForwardProcessor"/> (<c>http-forward</c>): forwards requests to another HTTP service through
    /// <c>IHttpClientFactory</c>. Restrict the targets with <see cref="DynamicHttpProcessorOptions.AllowedHosts"/>.
    /// </summary>
    public static IDynamicEndpointsBuilder AddHttpForwardProcessor(
        this IDynamicEndpointsBuilder builder,
        string? name = null,
        Action<DynamicHttpProcessorOptions>? configure = null)
    {
        AddHttp(builder, configure);
        return builder.AddProcessor(typeof(HttpForwardProcessor), name, description: null);
    }

    /// <summary>Registers <see cref="WebhookProcessor"/> (<c>webhook</c>): JSON webhooks with retries and optional HMAC signatures.</summary>
    public static IDynamicEndpointsBuilder AddWebhookProcessor(
        this IDynamicEndpointsBuilder builder,
        string? name = null,
        Action<DynamicHttpProcessorOptions>? configure = null)
    {
        AddHttp(builder, configure);
        builder.Services.TryAddSingleton<WebhookSender>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, WebhookBackgroundService>());
        return builder.AddProcessor(typeof(WebhookProcessor), name, description: null);
    }

    /// <summary>Registers <see cref="ResponseTemplateProcessor"/> (<c>response</c>): responses rendered from JSON or text templates.</summary>
    public static IDynamicEndpointsBuilder AddResponseTemplateProcessor(this IDynamicEndpointsBuilder builder, string? name = null) =>
        builder.AddProcessor(typeof(ResponseTemplateProcessor), name, description: null);

    private static void AddHttp(IDynamicEndpointsBuilder builder, Action<DynamicHttpProcessorOptions>? configure)
    {
        var options = builder.Services.AddOptions<DynamicHttpProcessorOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        builder.Services.AddHttpClient();
    }
}
