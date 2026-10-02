namespace DynamicEndpoints.Samples.Validation.Validators;

/// <summary>
/// A parameter validator in C#: the Polish tax id (NIP), 10 digits with a weighted checksum, dashes and spaces ignored.
/// <c>Targets</c> and <c>ParameterTypes</c> tell the panel where it can be attached (string parameters only), and a mismatch is
/// rejected on save. Runs together with the JSON Schema constraints.
/// </summary>
[DynamicValidator("nip", Description = "Polish tax id (NIP) with checksum.", Targets = DynamicValidatorTargets.Parameter, ParameterTypes = [ParameterType.String])]
public sealed class NipValidator : IDynamicValidator
{
    private static readonly int[] Weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];

    public ValueTask ValidateAsync(DynamicValidationContext context)
    {
        var digits = new string((context.GetValue<string>() ?? string.Empty).Where(c => c is not ('-' or ' ')).ToArray());
        if (digits.Length != 10 || !digits.All(char.IsAsciiDigit))
        {
            // The error belongs to the validated parameter; "nip.length" is the code clients can rely on.
            context.AddError(null, "A NIP has exactly 10 digits.", "nip.length");
        }
        else if (Weights.Select((w, i) => w * (digits[i] - '0')).Sum() % 11 != digits[9] - '0')
        {
            context.AddError(null, "Invalid NIP checksum.", "nip.checksum");
        }

        return ValueTask.CompletedTask;
    }
}
