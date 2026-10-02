using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>
/// Handles requests of dynamic endpoints after binding and validation succeeded.
/// This is the boundary where dynamic input becomes regular application code.
/// Implementations are resolved from the request scope, so they can depend on scoped services.
/// </summary>
public interface IDynamicEndpointProcessor
{
    Task<IResult> ProcessAsync(DynamicRequest request);

    /// <summary>Validates <see cref="DynamicEndpointDefinition.ProcessorConfig"/> when a definition is saved.</summary>
    IEnumerable<string> ValidateConfiguration(JsonObject configuration) => [];

    /// <summary>
    /// Validates the configuration with the definition it belongs to, e.g. to allow some settings only for some tenants.
    /// The library calls this overload; by default it calls <see cref="ValidateConfiguration(JsonObject)"/>.
    /// </summary>
    IEnumerable<string> ValidateConfiguration(JsonObject configuration, DynamicEndpointDefinition definition) => ValidateConfiguration(configuration);
}

/// <summary>
/// Base class for processors with a strongly typed configuration. The configuration is validated on save
/// (unknown properties and DataAnnotations violations are rejected) and deserialized once per endpoint version.
/// </summary>
public abstract class DynamicEndpointProcessor<TConfiguration> : IDynamicEndpointProcessor
    where TConfiguration : class, new()
{
    public Task<IResult> ProcessAsync(DynamicRequest request) =>
        ProcessAsync(request, request.GetConfiguration<TConfiguration>() ?? new TConfiguration());

    protected abstract Task<IResult> ProcessAsync(DynamicRequest request, TConfiguration configuration);

    public IEnumerable<string> ValidateConfiguration(JsonObject configuration) =>
        TypedConfiguration.Validate<TConfiguration>(configuration, Validate);

    public IEnumerable<string> ValidateConfiguration(JsonObject configuration, DynamicEndpointDefinition definition) =>
        TypedConfiguration.Validate<TConfiguration>(configuration, c => Validate(c, definition));

    /// <summary>Additional, custom configuration checks.</summary>
    protected virtual IEnumerable<string> Validate(TConfiguration configuration) => [];

    /// <summary>Checks that depend on the definition, e.g. its <see cref="DynamicEndpointDefinition.Tenant"/>. Calls <see cref="Validate(TConfiguration)"/> by default.</summary>
    protected virtual IEnumerable<string> Validate(TConfiguration configuration, DynamicEndpointDefinition definition) => Validate(configuration);
}

internal static class TypedConfiguration
{
    /// <summary>Strict deserialization (typos are errors) + DataAnnotations + custom checks.</summary>
    public static IReadOnlyList<string> Validate<TConfiguration>(JsonObject configuration, Func<TConfiguration, IEnumerable<string>> custom)
        where TConfiguration : class, new()
    {
        TConfiguration? config;
        try
        {
            config = configuration.Deserialize<TConfiguration>(DynamicEndpointsJson.StrictSerializerOptions);
        }
        catch (JsonException ex)
        {
            return [$"Invalid configuration: {ex.Message}"];
        }

        config ??= new TConfiguration();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(config, new ValidationContext(config), results, validateAllProperties: true);
        return results.Select(r => r.ErrorMessage ?? "Invalid configuration.").Concat(custom(config)).ToList();
    }
}

/// <summary>Provides the processor name and documentation shown in the admin API.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DynamicProcessorAttribute(string name) : Attribute
{
    public string Name { get; } = name;

    public string? Description { get; set; }

    /// <summary>Example configuration as JSON – shown to admins as a starting point.</summary>
    public string? ConfigurationExample { get; set; }
}

public sealed record DynamicProcessorDescriptor(string Name, string? Description, JsonObject? ConfigurationExample);
