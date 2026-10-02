# Getting started

```bash
dotnet add package DynamicEndpoints
dotnet add package DynamicEndpoints.EntityFrameworkCore   # persistence
dotnet add package DynamicEndpoints.FluentValidation      # optional
dotnet add package DynamicEndpoints.AdminUI               # optional: the admin panel
```

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite("Data Source=app.db"));

builder.Services
    .AddDynamicEndpoints()
    .AddFromAssemblyContaining<Program>()          // all processors, validators & seeders
    .UseEntityFrameworkStore<AppDbContext>();      // persistence

var app = builder.Build();

app.MapDynamicEndpoints();                                         // 🔥 the dynamic routes
app.MapDynamicEndpointsAdmin("/api/admin/endpoints")               // 🖥️ management API
   .RequireAuthorization("admin");
app.MapDynamicEndpointsAdminUI("/admin", "/api/admin/endpoints")   // 🎛️ admin panel (DynamicEndpoints.AdminUI)
   .RequireAuthorization("admin");
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");           // 📜 OpenAPI 3.1
app.UseSwaggerUI(c => c.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic API"));

app.Run();
```

Add the tables to your context with `modelBuilder.ApplyDynamicEndpointsConfiguration();` and you're done. Open `/admin/` for the
[admin panel](admin-ui.md).

#### Your first endpoint, from code

```csharp
public sealed class OrdersSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken ct)
    {
        if ((await manager.ListAsync(ct)).Count > 0) return;

        await manager.CreateAsync(DynamicEndpoint.Post("/orders/{customerId}")
            .Named("Create order")
            .InGroup("Orders")
            .HandledBy<OrderProcessor, OrderConfig>(new() { Queue = "incoming" })   // typed, no magic strings
            .FromRoute("customerId", p => p.String().Pattern("^C[0-9]{3}$"))
            .FromHeader("tenantId", p => p.BindFrom("X-Tenant-Id").Required())
            .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
            .FromBody("deliveryDate", p => p.Date().Required())
            .FromBody("contactEmail", p => p.Email().Required())
            .FromBody("nip", p => p.ValidatedBy<NipValidator>())
            .WithRule("""{ ">=": [{ "var": "quantity" }, 10] }""", "Wholesale orders only.", "quantity")
            .ValidatedBy<CreditLimitValidator>(), ct);
    }
}
```

…or let an admin click the same thing together in a panel. 🖱️

`HandledBy<T>()` and `ValidatedBy<T>()` resolve the name the same way registration does (the attribute, or the type name),
so code and registration can't drift apart. String names (`HandledBy("orders")`) still work. That's what the panel stores.

#### …and the code behind it

```csharp
[DynamicProcessor("orders", Description = "Puts orders on a queue")]
public sealed class OrderProcessor(IBus bus) : DynamicEndpointProcessor<OrderConfig>
{
    protected override async Task<IResult> ProcessAsync(DynamicRequest request, OrderConfig config)
    {
        // request.Parameters is already bound, converted and validated
        await bus.Send(config.Queue, request.Parameters, request.RequestAborted);
        return Results.Accepted();
    }
}
```

#### Run it

The setup above – without the authorization, so you can click around – is the [`01-QuickStart` sample](samples.md):
`dotnet run --project samples/01-QuickStart`, then open
`http://localhost:5101/admin/`. The [other samples](samples.md) take one feature area each – validation, processors, the
built-in processors, ef-crud, multi-tenancy, the OpenAPI import, GitOps, drafts and history, caching and rate limits,
telemetry – in a small app of its own.

#### Where to go next

- [Samples](samples.md): a runnable app per feature, with a README and a `.http` file.
- [Admin panel](admin-ui.md) and [drafts, history & rollback](drafts-and-history.md): let admins change endpoints safely.
- [Built-in processors](built-in-processors.md): forward, webhook, response templates and SQL without writing a processor.
- [Export, import & GitOps](export-import-gitops.md) and [import from OpenAPI](openapi-import.md): definitions in Git, pushed from CI.
- [Caching & rate limits](caching-and-rate-limits.md), [metrics & tracing](telemetry.md), [multiple instances](multiple-instances.md).
