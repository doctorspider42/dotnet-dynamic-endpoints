using System.Numerics;
using FluentValidation;

namespace DynamicEndpoints.Samples.Showcase.Validators;

/// <summary>
/// Plain DTO for FluentValidation. The bound parameters of an endpoint are deserialized into it,
/// so property names match parameter names (case-insensitive).
/// </summary>
public sealed record BookingRequest(DateOnly CheckIn, DateOnly CheckOut, int Guests, string RoomType);

/// <summary>Registered as "booking-request" (type name without the "Validator" suffix).</summary>
public sealed class BookingRequestValidator : AbstractValidator<BookingRequest>
{
    public BookingRequestValidator(TimeProvider timeProvider)
    {
        RuleFor(b => b.CheckIn)
            .GreaterThanOrEqualTo(_ => DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime))
            .WithMessage("Check-in cannot be in the past.");

        RuleFor(b => b.CheckOut)
            .Must((b, checkOut) => checkOut.DayNumber - b.CheckIn.DayNumber <= 14)
            .WithMessage("A stay cannot be longer than 14 nights.");

        RuleFor(b => b.Guests)
            .LessThanOrEqualTo(4)
            .When(b => b.RoomType == "double")
            .WithMessage("A double room fits at most 4 guests (with extra beds).");
    }
}

/// <summary>Scalar validator, so it attaches to a single parameter. Registered as "iban".</summary>
public sealed class IbanValidator : AbstractValidator<string>
{
    public IbanValidator()
    {
        RuleFor(iban => iban)
            .Must(BeValidIban)
            .WithMessage("Invalid IBAN.");
    }

    private static bool BeValidIban(string value)
    {
        var iban = value.Replace(" ", string.Empty).ToUpperInvariant();
        if (iban.Length is < 15 or > 34 || !iban.All(char.IsAsciiLetterOrDigit))
        {
            return false;
        }

        // Move the first four characters to the end, letters -> numbers (A=10), mod 97 must be 1.
        var rearranged = iban[4..] + iban[..4];
        var numeric = string.Concat(rearranged.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));
        return BigInteger.Parse(numeric) % 97 == 1;
    }
}
