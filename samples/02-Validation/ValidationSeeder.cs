using DynamicEndpoints.Samples.Validation.Validators;

namespace DynamicEndpoints.Samples.Validation;

/// <summary>
/// One endpoint per validation layer. They all use the inline "echo" processor of Program.cs, which answers with the validated,
/// normalized parameters – so every request either shows what the processor would get, or the errors.
/// </summary>
public sealed class ValidationSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        DynamicEndpointDefinition[] definitions =
        [
            // Layer 1 – constraints (JSON Schema), defined by admins: types, lengths, allowed values and the built-in formats.
            // All errors come back at once.
            DynamicEndpoint.Post("/contacts")
                .Named("Validate contact")
                .WithDescription("Constraints and built-in formats – no code needed: e-mail, phone (E.164), URI and time.")
                .InGroup("1. Constraints")
                .HandledBy("echo")
                .FromBody("name", p => p.String().Required().Length(2, 50).Example("Jan Kowalski"))
                .FromBody("email", p => p.Email().Required().Example("jan.kowalski@example.com"))
                .FromBody("phone", p => p.Phone().Example("+48123456789"))
                .FromBody("website", p => p.Uri().Example("https://example.com"))
                .FromBody("callAt", p => p.String().Format(ParameterFormat.Time).Example("09:30"))
                .FromBody("country", p => p.String().OneOf("PL", "DE", "US").Default("PL"))
                .FromBody("tags", p => p.ArrayOf(ParameterType.String).Items(0, 3).MaxLength(20)),

            // Layer 2 – parameter validators, written by developers and attached by admins: a C# checksum and a FluentValidation one.
            DynamicEndpoint.Post("/invoices")
                .Named("Validate invoice")
                .WithDescription("Parameter validators: NIP checksum (C#) and IBAN (FluentValidation).")
                .InGroup("2. Parameter validators")
                .HandledBy("echo")
                .FromBody("nip", p => p.String().Required().ValidatedBy<NipValidator>().Example("526-000-12-46"))
                .FromBody("iban", p => p.String().Required().ValidatedBy<IbanValidator>().Example("PL61 1090 1014 0000 0712 1981 2874"))
                .FromBody("amount", p => p.Number().Required().Min(0.01m).Example(100)),

            // Layer 3 – business rules (JsonLogic), defined by admins. They run when the constraints passed, and can carry a code.
            // Layer 4 – a request validator (FluentValidation, the whole request), runs last.
            DynamicEndpoint.Post("/bookings")
                .Named("Validate booking")
                .WithDescription("JsonLogic rules across fields, then a FluentValidation request validator.")
                .InGroup("3. Rules and request validators")
                .HandledBy("echo")
                .FromBody("roomType", p => p.String().Required().OneOf("single", "double", "suite"))
                .FromBody("guests", p => p.Integer().Required().Range(1, 6))
                .FromBody("checkIn", p => p.Date().Required().Example("2030-11-01"))
                .FromBody("checkOut", p => p.Date().Required().Example("2030-11-05"))
                .WithRule("""{ "<": [{ "var": "checkIn" }, { "var": "checkOut" }] }""", "Check-out must be after check-in.", "checkOut", code: "stayOrder")
                .WithRule("""{ "or": [{ "!=": [{ "var": "roomType" }, "single"] }, { "==": [{ "var": "guests" }, 1] }] }""",
                    "A single room fits exactly one guest.", "guests")
                .ValidatedBy<BookingRequestValidator>(),

            // Layer 4 – a request validator in C#, with a configuration of its own per endpoint.
            DynamicEndpoint.Post("/orders")
                .Named("Validate order")
                .WithDescription("A C# request validator with configuration: quantity × unitPrice ≤ maxTotal.")
                .InGroup("3. Rules and request validators")
                .HandledBy("echo")
                .FromBody("sku", p => p.String().Required().Pattern("^[A-Z]{3}-[0-9]{1,4}$").Example("ANV-1"))
                .FromBody("quantity", p => p.Integer().Required().Range(1, 1000).Example(2))
                .FromBody("unitPrice", p => p.Number().Required().Min(0).Example(49.9))
                .ValidatedBy<OrderTotalValidator, OrderTotalConfig>(new() { MaxTotal = 5000 }),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
