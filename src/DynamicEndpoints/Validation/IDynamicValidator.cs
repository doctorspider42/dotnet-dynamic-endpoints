using System.Text.Json.Nodes;

namespace DynamicEndpoints;

/// <summary>
/// Custom validation logic written in code and attached to endpoints by admins – the validation counterpart of
/// <see cref="IDynamicEndpointProcessor"/>. Resolved from the request scope, so it can use scoped services (e.g. a DbContext).
/// </summary>
public interface IDynamicValidator
{
    /// <summary>Reports problems through <see cref="DynamicValidationContext.AddError(string)"/>.</summary>
    ValueTask ValidateAsync(DynamicValidationContext context);

    /// <summary>Validates <see cref="ValidatorReference.Config"/> when a definition is saved.</summary>
    IEnumerable<string> ValidateConfiguration(JsonObject configuration) => [];
}

/// <summary>Base class for validators with a strongly typed configuration (validated on save like processor configuration).</summary>
public abstract class DynamicValidator<TConfiguration> : IDynamicValidator
    where TConfiguration : class, new()
{
    public ValueTask ValidateAsync(DynamicValidationContext context) =>
        ValidateAsync(context, context.GetConfiguration<TConfiguration>() ?? new TConfiguration());

    protected abstract ValueTask ValidateAsync(DynamicValidationContext context, TConfiguration configuration);

    public IEnumerable<string> ValidateConfiguration(JsonObject configuration) =>
        TypedConfiguration.Validate<TConfiguration>(configuration, Validate);

    protected virtual IEnumerable<string> Validate(TConfiguration configuration) => [];
}

/// <summary>Where a validator can be attached.</summary>
[Flags]
public enum DynamicValidatorTargets
{
    /// <summary>To a single parameter – the validator checks <see cref="DynamicValidationContext.Value"/>.</summary>
    Parameter = 1,
    /// <summary>To a whole endpoint – the validator checks <see cref="DynamicValidationContext.Parameters"/>.</summary>
    Request = 2,
    Any = Parameter | Request,
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DynamicValidatorAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public string? Description { get; set; }

    public string? ConfigurationExample { get; set; }

    public DynamicValidatorTargets Targets { get; set; } = DynamicValidatorTargets.Any;

    /// <summary>Parameter types this validator can check; empty means any. Mismatches are rejected when a definition is saved.</summary>
    public ParameterType[] ParameterTypes { get; set; } = [];
}

public sealed record DynamicValidatorDescriptor(
    string Name,
    string? Description,
    JsonObject? ConfigurationExample,
    bool ForParameters,
    bool ForRequests,
    IReadOnlyList<ParameterType>? ParameterTypes = null);
