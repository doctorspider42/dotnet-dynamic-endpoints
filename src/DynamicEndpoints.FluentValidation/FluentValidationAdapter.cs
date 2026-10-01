using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;

namespace DynamicEndpoints.FluentValidation;

/// <summary>
/// Runs an <see cref="IValidator{T}"/> as a dynamic endpoint validator. Request-level: the bound parameters are
/// deserialized into <typeparamref name="T"/>. Parameter-level (scalar <typeparamref name="T"/>): the parameter value is.
/// </summary>
internal sealed class FluentValidationAdapter<T>(IValidator<T> validator) : IDynamicValidator
{
    public async ValueTask ValidateAsync(DynamicValidationContext context)
    {
        T? model;
        try
        {
            JsonNode? source = context.Parameter is null ? context.Parameters : context.Value;
            model = source is null ? default : source.Deserialize<T>(DynamicEndpointsJson.SerializerOptions);
        }
        catch (JsonException ex)
        {
            // The definition and the model do not fit together – a configuration problem, reported instead of a 500.
            context.AddError($"The request cannot be validated by '{context.ValidatorName}': {ex.Message}");
            return;
        }

        if (model is null)
        {
            return;
        }

        var validationContext = new ValidationContext<T>(model);
        validationContext.RootContextData[DynamicFluentValidation.ContextKey] = context;

        var result = await validator.ValidateAsync(validationContext, context.RequestAborted);
        foreach (var failure in result.Errors.Where(f => f.Severity == Severity.Error))
        {
            // Parameter-level: everything belongs to the parameter. Request-level: "CheckOut" -> "checkOut", "Address.City" -> "address.city".
            context.AddError(context.Parameter is null ? ToPath(failure.PropertyName) : null, failure.ErrorMessage);
        }
    }

    private static string? ToPath(string? propertyName) =>
        string.IsNullOrEmpty(propertyName)
            ? null
            : string.Join('.', propertyName.Split('.').Select(segment => JsonNamingPolicy.CamelCase.ConvertName(segment)));
}

public static class DynamicFluentValidation
{
    /// <summary>Key of the <see cref="DynamicValidationContext"/> in <c>ValidationContext.RootContextData</c>.</summary>
    public const string ContextKey = "DynamicEndpoints.ValidationContext";

    /// <summary>
    /// Access to the dynamic endpoint (definition, validator configuration, HttpContext) from custom FluentValidation rules:
    /// <code>RuleFor(x => x.Amount).Custom((amount, ctx) => { var limit = ctx.GetDynamicContext()?.GetConfiguration&lt;Limits&gt;(); ... });</code>
    /// </summary>
    public static DynamicValidationContext? GetDynamicContext(this IValidationContext context) =>
        context.RootContextData.TryGetValue(ContextKey, out var value) ? value as DynamicValidationContext : null;
}
