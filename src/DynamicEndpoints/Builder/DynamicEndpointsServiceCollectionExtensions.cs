using DynamicEndpoints;
using DynamicEndpoints.Hosting;
using DynamicEndpoints.Management;
using DynamicEndpoints.Processing;
using DynamicEndpoints.Runtime;
using DynamicEndpoints.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.DependencyInjection;

public interface IDynamicEndpointsBuilder
{
    IServiceCollection Services { get; }
}

public static class DynamicEndpointsServiceCollectionExtensions
{
    public static IDynamicEndpointsBuilder AddDynamicEndpoints(
        this IServiceCollection services,
        Action<DynamicEndpointsOptions>? configure = null)
    {
        services.AddOptions<DynamicEndpointsOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.AddOptions<ProcessorRegistryOptions>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ProcessorRegistry>();
        services.AddOptions<ValidatorRegistryOptions>();
        services.TryAddSingleton<ValidatorRegistry>();
        services.TryAddSingleton<RouteInspector>();
        services.TryAddSingleton<DynamicEndpointDataSource>();
        services.TryAddSingleton<DynamicEndpointConventions>();
        services.TryAddSingleton<ParameterBinder>();
        services.TryAddSingleton<DynamicRequestHandler>();
        services.TryAddSingleton<DynamicEndpointCompiler>();
        services.TryAddSingleton<DynamicEndpointRuntime>();
        services.TryAddSingleton<IDynamicEndpointManager, DynamicEndpointManager>();
        services.TryAddSingleton<IDynamicOpenApiDocumentProvider, DynamicOpenApiDocumentProvider>();
        services.TryAddSingleton<IDynamicEndpointSnippetGenerator, DynamicEndpointSnippetGenerator>();
        services.TryAddSingleton<IDynamicEndpointTransfer, DynamicEndpointTransfer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDynamicEndpointsTextFormat, JsonDynamicEndpointsTextFormat>());
        services.TryAddSingleton<IDynamicEndpointStore, InMemoryDynamicEndpointStore>();
        services.TryAddSingleton<IDynamicErrorResponseFactory, DefaultDynamicErrorResponseFactory>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DynamicEndpointsHostedService>());
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IDynamicEndpointChangeHandler, OutputCacheEvictionHandler>());

        // Serves the rate limits defined in the definitions themselves; effective once the app calls AddRateLimiter/UseRateLimiter.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<RateLimiterOptions>, ConfigureDynamicRateLimiting>());

        return new DynamicEndpointsBuilder(services);
    }

    /// <summary>
    /// Registers a processor class. The name comes from <paramref name="name"/>, <see cref="DynamicProcessorAttribute"/>
    /// or the type name (<c>OrderLookupProcessor</c> → <c>order-lookup</c>). Processors are scoped.
    /// </summary>
    public static IDynamicEndpointsBuilder AddProcessor<TProcessor>(this IDynamicEndpointsBuilder builder, string? name = null, string? description = null)
        where TProcessor : class, IDynamicEndpointProcessor =>
        builder.AddProcessor(typeof(TProcessor), name, description);

    internal static IDynamicEndpointsBuilder AddProcessor(this IDynamicEndpointsBuilder builder, Type processorType, string? name, string? description)
    {
        var descriptor = ProcessorRegistry.Describe(processorType, name, description);
        builder.Services.Configure<ProcessorRegistryOptions>(o => o.Processors.Add(descriptor));
        builder.Services.AddKeyedScoped(typeof(IDynamicEndpointProcessor), descriptor.Name, processorType);
        return builder;
    }

    /// <summary>Registers an inline processor.</summary>
    public static IDynamicEndpointsBuilder AddProcessor(
        this IDynamicEndpointsBuilder builder,
        string name,
        Func<DynamicRequest, Task<IResult>> handler,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        builder.Services.Configure<ProcessorRegistryOptions>(o => o.Processors.Add(new DynamicProcessorDescriptor(name, description, null)));
        builder.Services.AddKeyedSingleton<IDynamicEndpointProcessor>(name, new DelegateProcessor(handler));
        return builder;
    }

    /// <summary>Registers an inline, synchronous processor.</summary>
    public static IDynamicEndpointsBuilder AddProcessor(
        this IDynamicEndpointsBuilder builder,
        string name,
        Func<DynamicRequest, IResult> handler,
        string? description = null) =>
        builder.AddProcessor(name, request => Task.FromResult(handler(request)), description);

    /// <summary>
    /// Registers a custom validator class admins can attach to parameters and/or endpoints. The name comes from
    /// <paramref name="name"/>, <see cref="DynamicValidatorAttribute"/> or the type name (<c>NipValidator</c> → <c>nip</c>). Validators are scoped.
    /// </summary>
    public static IDynamicEndpointsBuilder AddValidator<TValidator>(this IDynamicEndpointsBuilder builder, string? name = null, string? description = null)
        where TValidator : class, IDynamicValidator =>
        builder.AddValidator(typeof(TValidator), name, description);

    internal static IDynamicEndpointsBuilder AddValidator(this IDynamicEndpointsBuilder builder, Type validatorType, string? name, string? description)
    {
        var descriptor = ValidatorRegistry.Describe(validatorType, name, description);
        builder.Services.Configure<ValidatorRegistryOptions>(o => o.Validators.Add(descriptor));
        builder.Services.AddKeyedScoped(typeof(IDynamicValidator), descriptor.Name, validatorType);
        return builder;
    }

    /// <summary>Registers an inline validator.</summary>
    public static IDynamicEndpointsBuilder AddValidator(
        this IDynamicEndpointsBuilder builder,
        string name,
        Func<DynamicValidationContext, ValueTask> validate,
        DynamicValidatorTargets targets = DynamicValidatorTargets.Any,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(validate);
        var validator = new DelegateValidator(validate);
        return builder.AddValidator(
            new DynamicValidatorDescriptor(name, description, null,
                targets.HasFlag(DynamicValidatorTargets.Parameter), targets.HasFlag(DynamicValidatorTargets.Request)),
            (_, _) => validator);
    }

    /// <summary>Low-level registration for integrations (e.g. FluentValidation): a descriptor plus a scoped factory.</summary>
    public static IDynamicEndpointsBuilder AddValidator(
        this IDynamicEndpointsBuilder builder,
        DynamicValidatorDescriptor descriptor,
        Func<IServiceProvider, object?, IDynamicValidator> factory)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        builder.Services.Configure<ValidatorRegistryOptions>(o => o.Validators.Add(descriptor));
        builder.Services.AddKeyedScoped(descriptor.Name, factory);
        return builder;
    }

    /// <summary>
    /// Registers a seeder that creates initial endpoints on start-up. It is resolved from DI,
    /// so dependencies such as <see cref="IDynamicEndpointManager"/> are constructor-injected.
    /// </summary>
    public static IDynamicEndpointsBuilder AddSeeder<TSeeder>(this IDynamicEndpointsBuilder builder)
        where TSeeder : class, IDynamicEndpointSeeder =>
        builder.AddSeeder(typeof(TSeeder));

    internal static IDynamicEndpointsBuilder AddSeeder(this IDynamicEndpointsBuilder builder, Type seederType)
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped(typeof(IDynamicEndpointSeeder), seederType));
        return builder;
    }

    /// <summary>
    /// Adds a filter that runs for every dynamic endpoint request – before validation (access checks) and when a request is
    /// rejected (custom error format, logging, metering). Filters are scoped and run in registration order.
    /// </summary>
    public static IDynamicEndpointsBuilder AddFilter<TFilter>(this IDynamicEndpointsBuilder builder)
        where TFilter : class, IDynamicEndpointFilter
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IDynamicEndpointFilter, TFilter>());
        return builder;
    }

    /// <summary>Adds an inline filter – see <see cref="IDynamicEndpointFilter"/>.</summary>
    public static IDynamicEndpointsBuilder AddFilter(
        this IDynamicEndpointsBuilder builder,
        Func<DynamicEndpointRequestContext, ValueTask>? onRequest = null,
        Func<DynamicValidationFailedContext, ValueTask>? onValidationFailed = null)
    {
        builder.Services.AddSingleton<IDynamicEndpointFilter>(new DelegateFilter(onRequest, onValidationFailed));
        return builder;
    }

    /// <summary>
    /// Adds a handler that runs after endpoints were created, changed or deleted (audit, cache invalidation, …).
    /// Handlers are scoped and run in registration order – see <see cref="IDynamicEndpointChangeHandler"/>.
    /// </summary>
    public static IDynamicEndpointsBuilder AddChangeHandler<THandler>(this IDynamicEndpointsBuilder builder)
        where THandler : class, IDynamicEndpointChangeHandler
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IDynamicEndpointChangeHandler, THandler>());
        return builder;
    }

    /// <summary>Adds an inline change handler – see <see cref="IDynamicEndpointChangeHandler"/>.</summary>
    public static IDynamicEndpointsBuilder OnChanged(
        this IDynamicEndpointsBuilder builder,
        Func<DynamicEndpointChangedEvent, CancellationToken, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        builder.Services.AddSingleton<IDynamicEndpointChangeHandler>(new DelegateChangeHandler(handler));
        return builder;
    }

    /// <summary>
    /// Propagates changes to the other instances right away through <typeparamref name="TNotifier"/> (singleton), e.g.
    /// PostgreSQL <c>LISTEN/NOTIFY</c> or Redis pub/sub. Keep <see cref="DynamicEndpointsOptions.RefreshInterval"/> as a fallback.
    /// </summary>
    public static IDynamicEndpointsBuilder UseChangeNotifier<TNotifier>(this IDynamicEndpointsBuilder builder)
        where TNotifier : class, IDynamicEndpointChangeNotifier
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IDynamicEndpointChangeNotifier, TNotifier>());
        return builder;
    }

    /// <summary>Propagates changes to the other instances through <paramref name="notifier"/>.</summary>
    public static IDynamicEndpointsBuilder UseChangeNotifier(this IDynamicEndpointsBuilder builder, IDynamicEndpointChangeNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        builder.Services.Replace(ServiceDescriptor.Singleton(notifier));
        return builder;
    }

    /// <summary>Adds a step that prepares the store before definitions are loaded (e.g. migrations) – see <see cref="IDynamicEndpointStoreInitializer"/>.</summary>
    public static IDynamicEndpointsBuilder AddStoreInitializer<TInitializer>(this IDynamicEndpointsBuilder builder)
        where TInitializer : class, IDynamicEndpointStoreInitializer
    {
        builder.Services.TryAddEnumerable(ServiceDescriptor.Scoped<IDynamicEndpointStoreInitializer, TInitializer>());
        return builder;
    }

    /// <summary>
    /// Builds every error response with <typeparamref name="TFactory"/> (singleton) – see <see cref="IDynamicErrorResponseFactory"/>.
    /// Add <c>app.UseDynamicEndpointsErrorResponses()</c> to format empty 401/403/404 responses and exceptions the same way.
    /// </summary>
    public static IDynamicEndpointsBuilder UseErrorResponseFactory<TFactory>(this IDynamicEndpointsBuilder builder)
        where TFactory : class, IDynamicErrorResponseFactory
    {
        builder.Services.Replace(ServiceDescriptor.Singleton<IDynamicErrorResponseFactory, TFactory>());
        return builder;
    }

    /// <summary>Builds every error response with <paramref name="factory"/> – see <see cref="IDynamicErrorResponseFactory"/>.</summary>
    public static IDynamicEndpointsBuilder UseErrorResponses(this IDynamicEndpointsBuilder builder, Func<DynamicErrorContext, IResult> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        builder.Services.Replace(ServiceDescriptor.Singleton<IDynamicErrorResponseFactory>(new DelegateErrorResponseFactory(factory)));
        return builder;
    }

    /// <summary>Uses a custom persistence implementation (scoped).</summary>
    public static IDynamicEndpointsBuilder UseStore<TStore>(this IDynamicEndpointsBuilder builder)
        where TStore : class, IDynamicEndpointStore
    {
        builder.Services.Replace(ServiceDescriptor.Scoped<IDynamicEndpointStore, TStore>());
        return builder;
    }

    private sealed class DynamicEndpointsBuilder(IServiceCollection services) : IDynamicEndpointsBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
