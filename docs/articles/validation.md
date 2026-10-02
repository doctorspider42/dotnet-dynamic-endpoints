# Validation

<img src="../images/try-validation.jpg" alt="Three validation errors from three different layers in one response" width="720">

*One request, three layers: a JSON Schema range check (`amount`), a C# checksum validator (`nip`) and a FluentValidation validator (`iban`).*

| Layer | Who defines it | Example | Runs |
|---|---|---|---|
| **Constraints** (JSON Schema) | admin | types, ranges, lengths, enums, formats (e-mail, URI, phone…), regex | always, all errors at once |
| **Parameter validators** | developer (C# / FluentValidation), attached by admin | NIP, IBAN, PESEL checksums | together with constraints |
| **Business rules** (JsonLogic) | admin | `checkOut > checkIn`, "single room = 1 guest" | when the above passed |
| **Request validators** | developer, attached by admin | "title must be unique" (DB lookup), credit limits | last, only for otherwise valid requests |

Errors come back as RFC 9457 `ValidationProblemDetails`, keyed by the names the client used (`X-Tenant-Id`, `address.city`, `tags[1]`).
Each error also has a stable code (`required`, `minLength`, `format`, `fileSize`, …) for your own error format (see *Filters* below).
A body that isn't valid JSON or valid UTF-8 is a `400`, never a `500`.

## Custom C# validator


```csharp
[DynamicValidator("nip", Description = "Polish tax id with checksum", Targets = DynamicValidatorTargets.Parameter)]
public sealed class NipValidator : IDynamicValidator
{
    public ValueTask ValidateAsync(DynamicValidationContext context)
    {
        if (!IsValidNip(context.GetValue<string>()))
            context.AddError("Invalid NIP checksum.");
        return ValueTask.CompletedTask;
    }
}
```

Need configuration? Derive from `DynamicValidator<TConfig>`: the config is deserialized strictly (typos are errors), checked with DataAnnotations on save and cached per endpoint version.

## FluentValidation (separate package)


```csharp
public sealed record BookingRequest(DateOnly CheckIn, DateOnly CheckOut, int Guests, string RoomType);

public sealed class BookingRequestValidator : AbstractValidator<BookingRequest>   // → "booking-request" (whole request)
{
    public BookingRequestValidator(TimeProvider time) =>
        RuleFor(b => b.CheckOut).Must((b, o) => o.DayNumber - b.CheckIn.DayNumber <= 14).WithMessage("Max 14 nights.");
}

public sealed class IbanValidator : AbstractValidator<string> { /* … */ }        // → "iban" (single parameter)

builder.Services.AddDynamicEndpoints().AddFluentValidatorsFromAssemblyContaining<Program>();
```

- **Request-level:** the bound parameters are deserialized into the model, and property paths map back to the request names.
- **Parameter-level:** scalar validators (`AbstractValidator<string>`, `<decimal>`, …) validate a single parameter. The panel only offers them for compatible parameter types, and a mismatch is rejected on save.
- **Context in custom rules:** `ctx.GetDynamicContext()` gives access to the endpoint, the `HttpContext` and the configuration.
