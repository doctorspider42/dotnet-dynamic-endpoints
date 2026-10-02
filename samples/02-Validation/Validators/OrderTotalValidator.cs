using System.ComponentModel.DataAnnotations;

namespace DynamicEndpoints.Samples.Validation.Validators;

/// <summary>Configuration of one use of the validator – admins set it per endpoint, checked with DataAnnotations on save.</summary>
public sealed class OrderTotalConfig
{
    [Range(1, 1_000_000)]
    public decimal MaxTotal { get; set; } = 1000;
}

/// <summary>
/// A request-level validator in C# with a configuration: quantity × unit price must stay below the configured total. Request
/// validators run last, only for requests that passed the constraints, parameter validators and rules – so the values are there
/// and have the right types. A real one would look up a credit limit in the database (inject a DbContext in the constructor).
/// </summary>
[DynamicValidator("order-total",
    Description = "Rejects orders whose quantity × unitPrice is above maxTotal.",
    ConfigurationExample = """{ "maxTotal": 1000 }""",
    Targets = DynamicValidatorTargets.Request)]
public sealed class OrderTotalValidator : DynamicValidator<OrderTotalConfig>
{
    protected override ValueTask ValidateAsync(DynamicValidationContext context, OrderTotalConfig configuration)
    {
        var total = context.Get<decimal>("quantity") * context.Get<decimal>("unitPrice");
        if (total > configuration.MaxTotal)
        {
            context.AddError("quantity", $"The order total {total:0.00} is above the limit of {configuration.MaxTotal:0.00}.", "orderTotal");
        }

        return ValueTask.CompletedTask;
    }
}
