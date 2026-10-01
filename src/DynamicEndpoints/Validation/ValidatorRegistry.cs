using DynamicEndpoints.Processing;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Validation;

internal sealed class ValidatorRegistryOptions
{
    public List<DynamicValidatorDescriptor> Validators { get; } = [];
}

internal sealed class ValidatorRegistry
{
    private readonly Dictionary<string, DynamicValidatorDescriptor> _validators;

    public ValidatorRegistry(IOptions<ValidatorRegistryOptions> options)
    {
        _validators = new Dictionary<string, DynamicValidatorDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in options.Value.Validators)
        {
            if (!_validators.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException($"Dynamic endpoint validator '{descriptor.Name}' is registered more than once.");
            }
        }

        All = _validators.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<DynamicValidatorDescriptor> All { get; }

    public bool TryGet(string name, out DynamicValidatorDescriptor descriptor) =>
        _validators.TryGetValue(name, out descriptor!);

    public static DynamicValidatorDescriptor Describe(Type type, string? name, string? description)
    {
        var attribute = type.GetCustomAttributes(typeof(DynamicValidatorAttribute), false)
            .OfType<DynamicValidatorAttribute>()
            .FirstOrDefault();
        name ??= attribute?.Name ?? RegistryNames.Derive(type, "Validator");
        var targets = attribute?.Targets ?? DynamicValidatorTargets.Any;
        return new DynamicValidatorDescriptor(
            name,
            description ?? attribute?.Description,
            RegistryNames.ParseExample(attribute?.ConfigurationExample, name),
            targets.HasFlag(DynamicValidatorTargets.Parameter),
            targets.HasFlag(DynamicValidatorTargets.Request),
            attribute?.ParameterTypes is { Length: > 0 } types ? types : null);
    }
}

internal sealed class DelegateValidator(Func<DynamicValidationContext, ValueTask> validate) : IDynamicValidator
{
    public ValueTask ValidateAsync(DynamicValidationContext context) => validate(context);
}
