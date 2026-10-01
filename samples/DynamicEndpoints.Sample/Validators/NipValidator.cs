namespace DynamicEndpoints.Sample.Validators;

/// <summary>Polish tax identification number (NIP) – 10 digits with a weighted checksum. Dashes and spaces are ignored.</summary>
[DynamicValidator("nip", Description = "Polish tax id (NIP) with checksum.", Targets = DynamicValidatorTargets.Parameter, ParameterTypes = [ParameterType.String])]
public sealed class NipValidator : IDynamicValidator
{
    private static readonly int[] Weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];

    public ValueTask ValidateAsync(DynamicValidationContext context)
    {
        var digits = new string((context.GetValue<string>() ?? string.Empty).Where(c => c is not ('-' or ' ')).ToArray());
        if (digits.Length != 10 || !digits.All(char.IsAsciiDigit))
        {
            context.AddError("A NIP has exactly 10 digits.");
        }
        else if (Weights.Select((w, i) => w * (digits[i] - '0')).Sum() % 11 != digits[9] - '0')
        {
            context.AddError("Invalid NIP checksum.");
        }

        return ValueTask.CompletedTask;
    }
}
