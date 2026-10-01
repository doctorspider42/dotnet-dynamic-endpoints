# DynamicEndpoints.FluentValidation

Use [FluentValidation](https://www.nuget.org/packages/FluentValidation) validators in
[DynamicEndpoints](https://www.nuget.org/packages/DynamicEndpoints): runtime-defined HTTP endpoints for ASP.NET Core.
Developers write a validator once, and admins attach it to any endpoint or parameter at runtime.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

## Register

```csharp
builder.Services.AddDynamicEndpoints()
    .AddFluentValidatorsFromAssemblyContaining<Program>();   // or .AddFluentValidator<IbanValidator>("iban")
```

Names come from the type name (`BookingRequestValidator` → `booking-request`) or from `[DynamicValidator("…")]`.

## Request-level validators

The bound parameters are deserialized into your model. Error paths (`CheckOut`, `Address.City`) map back to the names used in the request.

```csharp
public sealed record BookingRequest(DateOnly CheckIn, DateOnly CheckOut, int Guests, string RoomType);

public sealed class BookingRequestValidator : AbstractValidator<BookingRequest>
{
    public BookingRequestValidator() =>
        RuleFor(b => b.CheckOut).Must((b, o) => o.DayNumber - b.CheckIn.DayNumber <= 14).WithMessage("Max 14 nights.");
}

DynamicEndpoint.Post("/bookings").ValidatedBy<BookingRequestValidator>();
```

## Parameter-level validators

Validators of scalar types (`AbstractValidator<string>`, `<decimal>`, …) validate a single parameter. Type compatibility
is checked when a definition is saved, so an e-mail validator can't end up on an integer parameter.

```csharp
public sealed class IbanValidator : AbstractValidator<string> { /* … */ }

DynamicEndpoint.Post("/invoices").FromBody("iban", p => p.Required().ValidatedBy<IbanValidator>());
```

Custom rules can reach the endpoint definition, the `HttpContext` and the configuration through `ctx.GetDynamicContext()`.

📖 [Documentation](https://github.com/doctorspider42/dotnet-dynamic-endpoints#readme) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
