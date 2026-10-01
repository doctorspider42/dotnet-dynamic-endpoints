using DynamicEndpoints;
using DynamicEndpoints.Hosting;
using DynamicEndpoints.Management;
using DynamicEndpoints.Processing;
using DynamicEndpoints.Runtime;
using DynamicEndpoints.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        services.TryAddSingleton<IDynamicEndpointStore, InMemoryDynamicEndpointStore>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, DynamicEndpointsHostedService>());

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
