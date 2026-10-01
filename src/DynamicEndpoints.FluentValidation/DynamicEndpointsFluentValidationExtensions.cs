using System.Reflection;
using System.Runtime.CompilerServices;
using DynamicEndpoints;
using DynamicEndpoints.FluentValidation;
using DynamicEndpoints.Processing;
using FluentValidation;

namespace Microsoft.Extensions.DependencyInjection;

public static class DynamicEndpointsFluentValidationExtensions
{
    /// <summary>
    /// Makes a FluentValidation validator available to admins. The name comes from <paramref name="name"/>,
    /// <see cref="DynamicValidatorAttribute"/> or the type name (<c>BookingRequestValidator</c> → <c>booking-request</c>).
    /// Validators of scalar types (<c>AbstractValidator&lt;string&gt;</c>) attach to parameters, all others to endpoints.
    /// </summary>
    public static IDynamicEndpointsBuilder AddFluentValidator<TValidator>(this IDynamicEndpointsBuilder builder, string? name = null, string? description = null)
        where TValidator : class, IValidator =>
        builder.AddFluentValidator(typeof(TValidator), name, description);

    /// <summary>
    /// Registers every concrete <see cref="IValidator{T}"/> implementation of an assembly.
    /// Validators already registered (e.g. with an explicit name) are skipped; <c>filter</c> returns <c>false</c> to skip a type.
    /// </summary>
    public static IDynamicEndpointsBuilder AddFluentValidatorsFromAssembly(this IDynamicEndpointsBuilder builder, Assembly assembly, Func<Type, bool>? filter = null)
    {
        var validatorTypes = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
                && !t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
                && FindModelType(t) is not null
                && (filter?.Invoke(t) ?? true))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);
        foreach (var type in validatorTypes)
        {
            var validatorInterface = typeof(IValidator<>).MakeGenericType(FindModelType(type)!);
            var registered = builder.Services.Any(d =>
                d.ServiceType == validatorInterface && d.IsKeyedService && d.KeyedImplementationType == type);
            if (!registered)
            {
                builder.AddFluentValidator(type, name: null, description: null);
            }
        }

        return builder;
    }

    /// <inheritdoc cref="AddFluentValidatorsFromAssembly"/>
    public static IDynamicEndpointsBuilder AddFluentValidatorsFromAssemblyContaining<T>(this IDynamicEndpointsBuilder builder, Func<Type, bool>? filter = null) =>
        builder.AddFluentValidatorsFromAssembly(typeof(T).Assembly, filter);

    private static IDynamicEndpointsBuilder AddFluentValidator(this IDynamicEndpointsBuilder builder, Type validatorType, string? name, string? description)
    {
        var modelType = FindModelType(validatorType)
            ?? throw new ArgumentException($"{validatorType} does not implement IValidator<T>.", nameof(validatorType));
        var attribute = validatorType.GetCustomAttribute<DynamicValidatorAttribute>();
        name ??= attribute?.Name ?? RegistryNames.Derive(validatorType, "Validator");
        description ??= attribute?.Description ?? $"FluentValidation: {validatorType.Name}";
        var scalar = IsScalar(modelType);

        var validatorInterface = typeof(IValidator<>).MakeGenericType(modelType);
        var adapterType = typeof(FluentValidationAdapter<>).MakeGenericType(modelType);
        builder.Services.AddKeyedScoped(validatorInterface, name, validatorType);

        return builder.AddValidator(
            new DynamicValidatorDescriptor(name, description, null, ForParameters: scalar, ForRequests: !scalar,
                ParameterTypes: scalar ? CompatibleParameterTypes(modelType) : null),
            (services, key) => (IDynamicValidator)Activator.CreateInstance(adapterType, services.GetRequiredKeyedService(validatorInterface, key))!);
    }

    private static Type? FindModelType(Type validatorType)
    {
        var models = validatorType.GetInterfaces()
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(i => i.GetGenericArguments()[0])
            .ToList();
        return models.Count == 1 ? models[0] : null;
    }

    /// <summary>Parameter types whose JSON values deserialize into <paramref name="type"/> – checked when a definition is saved.</summary>
    private static ParameterType[] CompatibleParameterTypes(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string))
        {
            return [ParameterType.String, ParameterType.Date, ParameterType.DateTime, ParameterType.Guid];
        }

        if (type == typeof(Guid))
        {
            return [ParameterType.Guid];
        }

        if (type == typeof(DateOnly))
        {
            return [ParameterType.Date];
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return [ParameterType.DateTime];
        }

        if (type == typeof(bool))
        {
            return [ParameterType.Boolean];
        }

        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float))
        {
            return [ParameterType.Number, ParameterType.Integer];
        }

        if (type.IsPrimitive) // integral types (bool and floating point handled above)
        {
            return [ParameterType.Integer];
        }

        return [ParameterType.String]; // enums, TimeOnly
    }

    private static bool IsScalar(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum ||
            type == typeof(string) || type == typeof(decimal) || type == typeof(Guid) ||
            type == typeof(DateOnly) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeOnly);
    }
}
