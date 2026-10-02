using DynamicEndpoints.Sample.Processors;
using DynamicEndpoints.Sample.Validators;

namespace DynamicEndpoints.Sample;

/// <summary>
/// Endpoints created on first start, so the panel is not empty. Shows the fluent builder API.
/// Registered with <c>.AddSeeder&lt;SampleEndpointsSeeder&gt;()</c> – the manager is constructor-injected.
/// </summary>
public sealed class SampleEndpointsSeeder(IDynamicEndpointManager manager, ILogger<SampleEndpointsSeeder> logger) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        DynamicEndpointDefinition[] definitions =
        [
            DynamicEndpoint.Get("/hello/{name}")
                .Named("Say hello")
                .InGroup("Demo")
                .HandledBy<TemplateProcessor, TemplateConfig>(new() { Template = "Hello, {{name}}! Greetings from a runtime-defined endpoint 👋" })
                .FromRoute("name", p => p.String().Length(2, 30).Pattern("^[A-Za-zÀ-ž-]+$").Description("Who to greet")),

            DynamicEndpoint.Get("/calc/sum")
                .Named("Sum two numbers")
                .InGroup("Demo")
                .HandledBy<CalculatorProcessor, CalculatorConfig>(new() { Operation = CalculatorOperation.Sum, Operands = ["a", "b"] })
                .FromQuery("a", p => p.Number().Required().Example(2))
                .FromQuery("b", p => p.Number().Required().Example(40)),

            DynamicEndpoint.Post("/notes")
                .Named("Create note")
                .WithDescription("Stores a note in the 'notes' collection.")
                .InGroup("Notes")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Insert })
                .FromBody("title", p => p.String().Required().Length(3, 100))
                .FromBody("content", p => p.String().MaxLength(2000))
                .FromBody("priority", p => p.Integer().Range(1, 5).Default(3))
                .FromBody("tags", p => p.ArrayOf(ParameterType.String).Items(0, 5).MaxLength(20))
                .FromBody("dueDate", p => p.Date())
                .WithRule(
                    """{ "or": [{ "<": [{ "var": "priority" }, 4] }, { "!!": [{ "var": "dueDate" }] }] }""",
                    "High priority notes (4-5) need a due date.",
                    parameter: "dueDate")
                .ValidatedBy<UniqueValueValidator, UniqueValueConfig>(new() { Collection = "notes", Field = "title" }),

            DynamicEndpoint.Get("/notes")
                .Named("List notes")
                .InGroup("Notes")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.List })
                .FromQuery("limit", p => p.Integer().Range(1, 100).Default(20)),

            DynamicEndpoint.Get("/notes/{id}")
                .Named("Get note")
                .InGroup("Notes")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Get })
                .FromRoute("id", p => p.Guid()),

            DynamicEndpoint.Post("/documents")
                .Named("Upload document")
                .WithDescription("A multipart/form-data upload. The echo shows the file metadata the processor gets; request.GetFile(\"file\") gives the content.")
                .InGroup("Demo")
                .HandledBy<EchoProcessor>()
                .FromForm("title", p => p.String().Required().Length(3, 100))
                .FromForm("file", p => p.File(maxSize: 5 * 1024 * 1024, "application/pdf", "image/*").Required()),

            DynamicEndpoint.Delete("/notes/{id}")
                .Named("Delete note")
                .InGroup("Notes")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Delete })
                .FromRoute("id", p => p.Guid()),

            DynamicEndpoint.Post("/bookings")
                .Named("Validate booking")
                .WithDescription("Shows header binding, enums and cross-field rules. Echoes the normalized request.")
                .InGroup("Demo")
                .HandledBy<EchoProcessor>()
                .FromHeader("tenantId", p => p.BindFrom("X-Tenant-Id").String().Required().Pattern("^[a-z0-9-]{3,32}$").Example("acme"))
                .FromBody("roomType", p => p.String().Required().OneOf("single", "double", "suite"))
                .FromBody("guests", p => p.Integer().Required().Range(1, 6))
                .FromBody("checkIn", p => p.Date().Required().Example("2026-11-01"))
                .FromBody("checkOut", p => p.Date().Required().Example("2026-11-05"))
                .WithRule("""{ "<": [{ "var": "checkIn" }, { "var": "checkOut" }] }""", "Check-out must be after check-in.", "checkOut")
                .WithRule("""{ "or": [{ "!=": [{ "var": "roomType" }, "single"] }, { "==": [{ "var": "guests" }, 1] }] }""",
                    "A single room fits exactly one guest.", "guests")
                .ValidatedBy<BookingRequestValidator>(),

            DynamicEndpoint.Post("/invoices")
                .Named("Validate invoice")
                .WithDescription("Custom parameter validators: NIP checksum (C#) and IBAN (FluentValidation).")
                .InGroup("Demo")
                .HandledBy<EchoProcessor>()
                .FromBody("nip", p => p.Required().ValidatedBy<NipValidator>().Example("526-000-12-46"))
                .FromBody("iban", p => p.Required().ValidatedBy<IbanValidator>().Example("PL61 1090 1014 0000 0712 1981 2874"))
                .FromBody("amount", p => p.Number().Required().Min(0.01m)),

            DynamicEndpoint.Post("/contacts")
                .Named("Validate contact")
                .WithDescription("Built-in string formats – no code needed: e-mail, phone (E.164), URI and time.")
                .InGroup("Demo")
                .HandledBy<EchoProcessor>()
                .FromBody("email", p => p.Email().Required().Example("jan.kowalski@example.com"))
                .FromBody("phone", p => p.Phone().Example("+48123456789"))
                .FromBody("website", p => p.Uri().Example("https://example.com"))
                .FromBody("callAt", p => p.String().Format(ParameterFormat.Time).Example("09:30")),

            DynamicEndpoint.Get("/time")
                .Named("Current time")
                .InGroup("Demo")
                .HandledBy("clock")
                .FromQuery("timeZone", p => p.String().Default("UTC").Example("Europe/Warsaw")),

            // Built-in "response" processor, with caching (Cache-Control, ETag/304, output cache) and a rate limit of its own.
            DynamicEndpoint.Get("/products/{sku}")
                .Named("Get product")
                .WithDescription("Cached for 60 s (ETag, 304 Not Modified, 30 s on the server) and limited to 5 requests per minute per client – try it a few times.")
                .InGroup("Built-in processors")
                .HandledBy("response", new { statusCode = 200, body = new { sku = "{{sku}}", name = "Product {{sku}}", price = 9.99, currency = "EUR" } })
                .FromRoute("sku", p => p.String().Pattern("^[A-Z0-9-]{3,20}$").Example("ABC-123"))
                .Cached(TimeSpan.FromSeconds(60), eTag: true, outputCache: TimeSpan.FromSeconds(30))
                .RateLimited(5, TimeSpan.FromMinutes(1), quota: new DynamicEndpointQuota { Limit = 1000, Period = QuotaPeriod.Day }),

            // Built-in "sql-query" processor (DynamicEndpoints.Sql) on the sample database: the documents of the "collection" processor.
            DynamicEndpoint.Get("/reports/documents")
                .Named("Documents report (SQL)")
                .WithDescription("A read-only, parameterized SQL query – request values are always bound as parameters.")
                .InGroup("Built-in processors")
                .HandledBy("sql-query", new
                {
                    query = """SELECT "Id" AS id, "Data" AS data, "CreatedAt" AS "createdAt" FROM "Documents" WHERE "Collection" = @collection ORDER BY "CreatedAt" DESC""",
                    result = "Rows",
                    maxRows = 50,
                })
                .FromQuery("collection", p => p.String().Default("notes").Example("notes")),

            // An endpoint of one tenant: only requests with "X-Tenant: acme" see it. Manage it at /admin/tenants/acme/.
            DynamicEndpoint.Get("/welcome")
                .Named("Tenant welcome")
                .InGroup("Tenants")
                .HandledBy("response", new { text = "Welcome to Acme, {{name}}!" })
                .FromQuery("name", p => p.String().MaxLength(50).Default("guest"))
                .Build() with { Tenant = "acme" },
        ];

        var seeded = 0;
        foreach (var definition in definitions)
        {
            try
            {
                // A stable id per route: replicas seeding at the same time (Aspire, docker compose) can't create duplicates.
                await manager.CreateAsync(definition with { Id = StableId(definition) }, cancellationToken);
                seeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Sample endpoint {Method} {Route} was not seeded – another instance was faster.", definition.Method, definition.Route);
            }
        }

        logger.LogInformation("Seeded {Count} sample endpoints.", seeded);
    }

    private static Guid StableId(DynamicEndpointDefinition definition) =>
        new(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"{definition.Method} {definition.Route}")));
}
