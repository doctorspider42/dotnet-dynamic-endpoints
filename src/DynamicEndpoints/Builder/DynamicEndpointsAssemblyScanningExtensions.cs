using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicEndpoints;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers all processors, validators and seeders of an assembly – no need to list them one by one.
/// Types that are already registered (e.g. with an explicit name) are skipped, so scanning is safe to combine
/// with manual registrations and to call more than once.
/// </summary>
public static class DynamicEndpointsAssemblyScanningExtensions
{
    /// <summary>Registers every processor, validator and seeder found in the assembly. <c>filter</c> returns <c>false</c> to skip a type.</summary>
    public static IDynamicEndpointsBuilder AddFromAssembly(this IDynamicEndpointsBuilder builder, Assembly assembly, Func<Type, bool>? filter = null) =>
        builder
            .AddProcessorsFromAssembly(assembly, filter)
            .AddValidatorsFromAssembly(assembly, filter)
            .AddSeedersFromAssembly(assembly, filter);

    /// <inheritdoc cref="AddFromAssembly"/>
    public static IDynamicEndpointsBuilder AddFromAssemblyContaining<T>(this IDynamicEndpointsBuilder builder, Func<Type, bool>? filter = null) =>
        builder.AddFromAssembly(typeof(T).Assembly, filter);

    /// <summary>Registers every concrete <see cref="IDynamicEndpointProcessor"/> of <paramref name="assembly"/>.</summary>
    public static IDynamicEndpointsBuilder AddProcessorsFromAssembly(this IDynamicEndpointsBuilder builder, Assembly assembly, Func<Type, bool>? filter = null)
    {
        foreach (var type in FindImplementations<IDynamicEndpointProcessor>(assembly, filter))
        {
            if (!IsRegisteredKeyed<IDynamicEndpointProcessor>(builder.Services, type))
            {
                builder.AddProcessor(type, name: null, description: null);
            }
        }

        return builder;
    }

    /// <summary>Registers every concrete <see cref="IDynamicValidator"/> of <paramref name="assembly"/>.</summary>
    public static IDynamicEndpointsBuilder AddValidatorsFromAssembly(this IDynamicEndpointsBuilder builder, Assembly assembly, Func<Type, bool>? filter = null)
    {
        foreach (var type in FindImplementations<IDynamicValidator>(assembly, filter))
        {
            if (!IsRegisteredKeyed<IDynamicValidator>(builder.Services, type))
            {
                builder.AddValidator(type, name: null, description: null);
            }
        }

        return builder;
    }

    /// <summary>Registers every concrete <see cref="IDynamicEndpointSeeder"/> of <paramref name="assembly"/>.</summary>
    public static IDynamicEndpointsBuilder AddSeedersFromAssembly(this IDynamicEndpointsBuilder builder, Assembly assembly, Func<Type, bool>? filter = null)
    {
        foreach (var type in FindImplementations<IDynamicEndpointSeeder>(assembly, filter))
        {
            builder.AddSeeder(type); // TryAddEnumerable – duplicates are ignored
        }

        return builder;
    }

    private static IEnumerable<Type> FindImplementations<TService>(Assembly assembly, Func<Type, bool>? filter) =>
        assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && typeof(TService).IsAssignableFrom(t)
                && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && (filter?.Invoke(t) ?? true))
            .OrderBy(t => t.FullName, StringComparer.Ordinal); // deterministic order (matters for seeders)

    private static bool IsRegisteredKeyed<TService>(IServiceCollection services, Type implementation) =>
        services.Any(d => d.ServiceType == typeof(TService) && d.IsKeyedService && d.KeyedImplementationType == implementation);
}
