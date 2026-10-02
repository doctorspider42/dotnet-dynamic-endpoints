<div align="center">

# ⚡ DynamicEndpoints

### Runtime-defined HTTP endpoints for ASP.NET Core.
**Click it in a panel → it's live. Restart the app → it's still there.**

[![CI](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml/badge.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/DynamicEndpoints?logo=nuget&color=004880)](https://www.nuget.org/packages/DynamicEndpoints)
[![Dependencies](https://img.shields.io/badge/core%20dependencies-0-brightgreen)](#-packages)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-minimal%20APIs-512BD4?logo=dotnet&logoColor=white)](https://learn.microsoft.com/aspnet/core/fundamentals/minimal-apis)
[![OpenAPI 3.1](https://img.shields.io/badge/OpenAPI-3.1-6BA539?logo=openapiinitiative&logoColor=white)](https://spec.openapis.org/oas/v3.1.0)
[![JSON Schema 2020-12](https://img.shields.io/badge/JSON%20Schema-2020--12-blue)](https://json-schema.org/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](#-contributing)
[![GitHub stars](https://img.shields.io/github/stars/doctorspider42/dotnet-dynamic-endpoints?style=social)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/stargazers)

[Quick start](#-quick-start) •
[Features](#-features) •
[How it works](#-how-it-works) •
[Validation](#%EF%B8%8F-validation-four-layers-zero-recompiles) •
[Docs](#-documentation) •
[Changelog](CHANGELOG.md) •
[Sample app](#-sample-app)

<img src="docs/images/admin-list.jpg" alt="Admin panel with runtime-defined endpoints" width="820">

</div>

---

## 🤔 Why?

Your client wants to **define, publish and change HTTP endpoints from an admin panel**, with their own parameters,
their own validation and no deployment. The usual answers are bad:

- ❌ **"We'll add it in the next sprint"**: every new endpoint is a release.
- ❌ **"Admins can write C# in a textbox"**: that's remote code execution shipped as a feature.
- ❌ **A catch-all route with a hand-rolled dispatcher**: you lose authorization, rate limiting, OpenAPI and everything else routing gives you.

**DynamicEndpoints** takes a fourth path. Admins *configure* endpoints and developers write the *building blocks*.
Endpoints are real ASP.NET Core `RouteEndpoint`s, created and removed at runtime, persisted in your database and documented in Swagger.

```
admin clicks "publish"  →  validated  →  persisted  →  routable on every instance. No restart, no recompile.
```

## ✨ Features

| | |
|---|---|
| 🔥 **Hot endpoints** | Add, change, disable and delete endpoints at runtime. Routing swaps atomically, so a request never sees a half-applied state. |
| 📝 **Drafts & history** | Save a change as a draft, publish it now or at a set time. Every revision is kept: diff any two, roll back with one call. |
| 💾 **Persistent** | Stored with EF Core (any provider) and loaded on start-up. Optimistic concurrency, ready-made migrations, and saving in *your* transaction. |
| 🧩 **Declarative binding** | Route, query, header, JSON body and form parameters with types, defaults and request names (`X-Tenant-Id` → `tenantId`). |
| 📎 **File uploads** | `multipart/form-data` with size and content-type limits, streamed by ASP.NET Core. No base64, documented as binary in OpenAPI. |
| 🛡️ **Four layers of validation** | JSON Schema constraints, custom C# validators, JsonLogic business rules, FluentValidation. |
| 🪝 **Filters** | Access checks before validation, logging and metering of rejected requests. |
| 🧯 **One error format** | `IDynamicErrorResponseFactory` builds validation errors, 401/403/404/405 and exceptions in your own format. |
| 🌍 **Localized errors** | English and Polish built in, every message overridable, stable error codes for clients. |
| 📧 **Built-in formats** | E-mail, URI, phone (E.164), IPv4/IPv6, time, date, date-time, UUID. No code needed. |
| 🪶 **Zero dependencies** | The core depends on ASP.NET Core only. The JSON Schema subset and the JsonLogic engine are built in. |
| ⚙️ **Processors** | Your code, your DI, your database. Validated input goes to a regular, statically typed handler. |
| 📜 **OpenAPI 3.1 + Swagger UI** | Generated from the definitions and always current. Business rules show up in the docs. |
| 🔐 **First-class citizens** | Authorization policies, rate limiting, CORS and endpoint conventions behave the same as on hand-written endpoints. |
| 🚦 **Safe by design** | No code execution, ReDoS-proof regexes, body and depth limits, reserved prefixes, conflict detection. |
| 🌐 **Multi-instance** | Instant propagation through PostgreSQL `LISTEN/NOTIFY` or Redis pub/sub, with polling as the fallback. |
| 📣 **Change events** | Created / updated / deleted handlers for audit logs and cache invalidation. |
| 📈 **Metrics & tracing** | Requests, validation failures by layer, processor duration and errors per endpoint, plus spans for binding, every validation layer and the processor. Plain `System.Diagnostics`, ready for OpenTelemetry. |
| 🧪 **Test kit** | In-memory store for `WebApplicationFactory` and a ready-made test server. No database needed. |
| 🪄 **Assembly scanning** | `AddFromAssemblyContaining<Program>()` registers every processor, validator and seeder in one call. |
| 🖥️ **Admin REST API** | One line, `MapDynamicEndpointsAdmin()`, or build your own on top of `IDynamicEndpointManager`. |
| 🎛️ **Admin panel** | `MapDynamicEndpointsAdminUI()`: editor, drafts, history with diffs and rollback, and a "Try" console that writes curl, HTTPie and C# for you. |
| ✅ **Tested** | Integration tests run on TestServer + SQLite, and on real PostgreSQL and Redis containers: persistence, migrations, multiple instances, concurrency. |

## 🚀 Quick start

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

Add the table to your context with `modelBuilder.ApplyDynamicEndpointsConfiguration();` and you're done.

### Your first endpoint, from code

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

### …and the code behind it

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

## 🧠 How it works

```mermaid
flowchart LR
    A[HTTP request] --> B{Route match<br/><i>dynamic EndpointDataSource</i>}
    B --> P[Filters: OnRequestAsync<br/><i>access checks</i>]
    P --> C[Binding<br/>route · query · header · body · form]
    C --> D[Type conversion]
    D --> E[JSON Schema<br/>+ parameter validators]
    E --> F[JsonLogic<br/>business rules]
    F --> G[Request validators<br/><i>DB lookups etc.</i>]
    G --> H[IDynamicEndpointProcessor<br/><b>your code</b>]
    H --> I[IResult]
    P -. short-circuit .-> Y[e.g. 403]
    C -. 413 / 415 / bad body .-> X
    E -. errors .-> X[Filters: OnValidationFailedAsync<br/>default: 400 ValidationProblemDetails]
    F -. errors .-> X
    G -. errors .-> X
```

```mermaid
sequenceDiagram
    actor Admin
    participant API as Admin API / your code
    participant M as IDynamicEndpointManager
    participant DB as Store (EF Core)
    participant R as ASP.NET Core routing
    Admin->>API: POST definition
    API->>M: CreateAsync(definition)
    M->>M: compile & validate (schema, regex, rules, processor, conflicts)
    M->>DB: persist
    M->>R: swap endpoint list + fire change token
    R-->>Admin: endpoint is live ⚡
```

- **Routing.** A custom `EndpointDataSource` with a change token. Each change builds a complete new endpoint list and swaps it in with a single assignment.
- **Compile once.** On save, a definition is validated as a whole and compiled: route pattern, schema, regexes, rules, processor and validator configs, validator ↔ parameter type compatibility. A broken definition never reaches the routing table.
- **Built-in engines.** A JSON Schema 2020-12 subset (`type`, `properties`, `required`, `additionalProperties`, `items`, length/range/count limits, `enum`, `const`, `format`, `uniqueItems`, `multipleOf`) and a [JsonLogic](https://jsonlogic.com) evaluator with the reference JavaScript semantics. Unsupported schema keywords and unknown operators are **rejected on save**, never silently ignored.
- **Conflicts.** Detected on save: `/orders/{id}` vs `/Orders/{orderId:int}`, clashes with the app's own endpoints, reserved prefixes.
- **Persistence.** Searchable fields are columns, the full definition is JSON, so the model can grow without migrations. `Revision` is a DB concurrency token.

## 🛡️ Validation: four layers, zero recompiles

<img src="docs/images/try-validation.jpg" alt="Three validation errors from three different layers in one response" width="720">

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

<details>
<summary><b>Custom C# validator</b></summary>

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
</details>

<details>
<summary><b>FluentValidation</b> (separate package)</summary>

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
</details>

## 📚 Documentation

<details open>
<summary><b>Definition model</b></summary>

| Area | What admins can set |
|---|---|
| Endpoint | method, route template (`/orders/{id}`), name, description, group (section in Swagger UI), enabled |
| Parameters | source (`Route`, `Query`, `Header`, `Body`, `Form`), name in request, type (`String`, `Integer`, `Number`, `Boolean`, `Date`, `DateTime`, `Guid`, `Array`, `Object`, `File`), required, default, example |
| Constraints | min/max length, minimum/maximum, regex pattern, allowed values, min/max items, custom JSON Schema for objects/arrays, max file size, allowed content types |
| Formats | `Email`, `Uri`, `Phone` (E.164), `Ipv4`, `Ipv6`, `Time` for strings; `Date`, `DateTime`, `Guid` as types |
| Rules | [JsonLogic](https://jsonlogic.com) conditions with an error message, an optional error code and a target parameter |
| Custom validators | code validators attached to parameters or to the whole request, with optional configuration |
| Processing | processor name and configuration (JSON) |
| Security | allow anonymous, require authorization, authorization policy, rate limiting policy |
| Docs | response schema, request and response examples (documentation only) |
</details>

<details>
<summary><b>Managing endpoints: <code>IDynamicEndpointManager</code></b></summary>

Every operation validates, persists and swaps the routing table atomically. Inject it anywhere:

```csharp
await manager.CreateAsync(definition);
await manager.UpdateAsync(definition with { Route = "/v2/orders" });   // optimistic concurrency via Revision
await manager.UpsertAsync(definition);                                 // create or replace by Id, no revision needed
await manager.SetEnabledAsync(id, false);
await manager.DeleteAsync(id);
var check = await manager.ValidateAsync(definition);                   // dry run
await manager.ReloadAsync();                                           // re-read the store
```

`UpsertAsync` is made for syncing definitions from your own model: it creates the endpoint, or replaces the stored one with the same
`Id` whatever its revision. Nothing is written (and the revision stays) when the content didn't change.

Drafts, history and rollback are on the manager too, see *Drafts, history &amp; rollback* below.

The sample's `Greetings/` folder shows a purpose-built API on top of the manager: `POST /api/greetings {"slug":"pirate","greeting":"Ahoy"}` publishes `GET /greetings/pirate/{name}` immediately.
</details>

<details>
<summary><b>Drafts, history &amp; rollback</b></summary>

A change doesn't have to go live right away. Save it as a **draft**: it's validated like any definition, but routing keeps
serving the published revision until you publish the draft, by hand or at a set time.

```csharp
var draft = await manager.SaveDraftAsync(current with { Route = "/v2/orders" });   // based on current.Revision
await manager.SaveDraftAsync(DynamicEndpoint.Get("/promo").HandledBy("echo"),      // a new endpoint, drafted…
    publishAt: new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.Zero), comment: "Christmas promo");   // …and scheduled
var changes = await manager.DiffDraftAsync(draft.EndpointId);                      // what publishing would change
await manager.PublishAsync(draft.EndpointId);                                      // live – as the next revision

var history = await manager.GetHistoryAsync(id);                                   // every revision, newest first
var diff = await manager.DiffAsync(id, fromRevision: 3, toRevision: 5);            // [{ path: "parameters[quantity].maximum", kind: "Changed", from: 10, to: 100 }]
await manager.RollbackAsync(id, revision: 3);                                      // revision 3's content as revision 6
```

- **History:** every create, update, enable/disable, publish and rollback is a revision with its kind and comment. A rollback
  adds a revision instead of rewriting history. Deleting an endpoint deletes its history and draft.
- **Drafts:** at most one per endpoint. `ListAsync` shows them (`state.Draft`), and never-published endpoints have the status
  `Draft`. A draft remembers the revision it's based on (`BaseRevision`). If someone changed the endpoint in the meantime,
  publishing fails with a `409` / `DynamicEndpointConcurrencyException`, so save the draft again on top of the current revision.
- **Scheduled publishing:** drafts with `PublishAt` are published every `options.ScheduledPublishInterval` (10 s), exactly once
  across instances. A draft that can't be published any more (e.g. its route is taken) is logged and unscheduled. Call
  `PublishDueAsync()` from your own scheduler if you turn the interval off.
- **Diffs** address parameters and validators by name (`parameters[quantity].maximum`), so inserting one doesn't make
  everything look changed. `DynamicEndpointDiff.Compare(a, b)` compares any two definitions.
- **Transactions:** change sets have `SaveDraftAsync`, `PublishAsync`, `RollbackAsync` and `DiscardDraftAsync` too.
- **Stores:** the in-memory and EF Core stores keep history and drafts. A custom store opts in by implementing
  `IDynamicEndpointRevisionStore`, and without it these calls throw `NotSupportedException` (`501` in the admin API).
</details>

<details>
<summary><b>Saving endpoints in your own transaction: <code>BeginChanges</code></b></summary>

When an endpoint belongs to a row of your own (a feature version, a tenant setting), save both atomically. A change set writes
through the store you give it and touches the routing table only when you apply it, after your commit:

```csharp
var changes = manager.BeginChanges(db.GetDynamicEndpointStore());   // EF Core: tracked by your DbContext, not saved
await changes.UpsertAsync(definition, ct);                          // validated and compiled right away
db.FeatureVersions.Add(version);
await db.SaveChangesAsync(ct);                                      // one SaveChanges, one transaction
await changes.ApplyAsync(ct);                                       // routing table, change handlers, other instances
```

- **Rollback:** drop the change set. Nothing was routed, so there's nothing to undo.
- **Concurrency:** a stale revision fails inside *your* `SaveChanges` with `DbUpdateConcurrencyException`.
- **Explicit transactions:** `manager.BeginChanges(HttpContext.RequestServices)` uses the registered store with *your* scoped
  DbContext, and its saves join `db.Database.BeginTransactionAsync()`. `db.GetDynamicEndpointStore(saveChanges: true)` does the same
  for any context.
- **Route conflicts** are checked across the whole change set too.
</details>

<details>
<summary><b>Admin REST API: <code>MapDynamicEndpointsAdmin()</code></b></summary>

| Method | Path | |
|---|---|---|
| `GET` | `/` | all definitions with runtime status (`Active`, `Disabled`, `Invalid`, `Pending`) |
| `GET` | `/{id}` | single definition |
| `POST` | `/` | create & publish |
| `PUT` | `/{id}` | replace (requires matching `revision`) |
| `DELETE` | `/{id}` | delete |
| `POST` | `/{id}/enable` · `/{id}/disable` | toggle |
| `POST` | `/validate` | dry run |
| `POST` | `/reload` | re-read the store |
| `GET` | `/processors` · `/validators` | building blocks for the UI |
| `GET` · `POST` | `/drafts` | all drafts · draft a new endpoint |
| `GET` · `PUT` · `DELETE` | `/{id}/draft` | the draft of an endpoint (`{ definition, publishAt, comment }`) |
| `GET` | `/{id}/draft/diff` | what publishing would change |
| `POST` | `/{id}/publish` | publish the draft |
| `GET` | `/{id}/revisions` · `/{id}/revisions/{revision}` | history |
| `GET` | `/{id}/diff?from=3&to=5` | differences between revisions (`to` defaults to the published one) |
| `POST` | `/{id}/revisions/{revision}/rollback` | roll back |

It returns a `RouteGroupBuilder`, so secure it like any group: `.RequireAuthorization("admin")`. The prefix is reserved automatically.
</details>

<details>
<summary><b>Admin panel: <code>MapDynamicEndpointsAdminUI()</code></b></summary>

The `DynamicEndpoints.AdminUI` package serves the panel from embedded files, with no static files middleware and no dependencies:

```csharp
app.MapDynamicEndpointsAdmin("/api/admin/endpoints").RequireAuthorization("admin");
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", o =>
{
    o.Title = "Orders API – endpoints";
    o.SwaggerUrl = "/swagger";
    o.OpenApiUrl = "/openapi/dynamic.json";
}).RequireAuthorization("admin");
```

- **List & editor:** everything a definition has, with *Validate*, *Save draft* (with an optional publish time and comment) and *Save & publish*.
- **History:** revisions with kind and comment, diffs against the previous or the published revision, one-click rollback.
- **Try console:** example values generated from the parameters' examples, defaults, allowed values, formats, lengths, ranges and
  JSON Schemas, plus *Copy as curl / HTTPie / C# HttpClient*.
- Returns a `RouteGroupBuilder`, so `.RequireAuthorization()` works. The prefix is reserved and path bases are respected.
  The panel holds no data, so secure the admin API in any case.
</details>

<details>
<summary><b>Processors, validators, seeders &amp; assembly scanning</b></summary>

```csharp
builder.Services.AddDynamicEndpoints()
    .AddFromAssemblyContaining<Program>()                    // everything at once…
    .AddProcessorsFromAssembly(typeof(Program).Assembly, t => t.Namespace != "Legacy")   // …or filtered
    .AddProcessor<OrderProcessor>("orders-v2")               // explicit name (scanning then skips the type)
    .AddProcessor("ping", r => Results.Ok("pong"))           // inline
    .AddValidator("even", ctx => { /* … */ return ValueTask.CompletedTask; }, DynamicValidatorTargets.Parameter)
    .AddSeeder<OrdersSeeder>();
```

- **Names:** come from `[DynamicProcessor("…")]` / `[DynamicValidator("…")]` or the type name (`OrderLookupProcessor` → `order-lookup`).
- **Idempotent scanning:** a type that is already registered is skipped.
- **Single entry point:** register one processor and set `options.DefaultProcessor`.
</details>

<details>
<summary><b>File uploads &amp; forms</b></summary>

`Form` parameters read `multipart/form-data` or `application/x-www-form-urlencoded` bodies. Text fields are converted like query
parameters. Files are streamed by ASP.NET Core (to disk above a small threshold), so there is no base64 and no double buffering.

```csharp
DynamicEndpoint.Post("/documents")
    .HandledBy<DocumentProcessor>()
    .FromForm("title", p => p.Required().MaxLength(100))
    .FromForm("document", p => p.File(maxSize: 10 * 1024 * 1024, "application/pdf", "image/*").Required())
    .FromForm("attachments", p => p.Files(maxSize: 1024 * 1024).Items(0, 5));

// in the processor (or a validator)
var file = request.GetFile("document");                 // IFormFile
await using var stream = file!.OpenReadStream();
```

- In `request.Parameters` (and in JsonLogic rules) a file is its metadata: `{ "fileName", "contentType", "length" }`.
- `AllowedContentTypes` is checked against the `Content-Type` the client sent. Inspect the content when it matters.
- An endpoint reads either a JSON body or a form, not both. The overall form size limit is `MaxFormBodySize` (30 MB).
- OpenAPI documents the body as `multipart/form-data` with `format: binary` file fields, so Swagger UI shows a file picker.
</details>

<details>
<summary><b>Filters: access checks, error format, metering</b></summary>

```csharp
builder.Services.AddDynamicEndpoints().AddFilter<TenantFeatureFilter>();   // scoped, run in registration order

public sealed class TenantFeatureFilter(ITenantFeatures features, IMeter meter) : IDynamicEndpointFilter
{
    // After routing and authorization, before the body is read or validated.
    public async ValueTask OnRequestAsync(DynamicEndpointRequestContext context)
    {
        if (!await features.HasAccessAsync(context.HttpContext, context.Endpoint.Definition.Group))
            context.Result = Results.Problem(statusCode: 403, title: "Feature not available");   // short-circuits
    }

    // Validation errors (400), malformed or undecodable bodies (400), 413 and 415.
    public ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context)
    {
        meter.Rejected(context.Endpoint.Id, context.Reason);
        context.Result = Results.Json(new
        {
            apiVersion = "1.0",
            error = new { code = "VALIDATION_FAILED", message = context.Title,
                          details = context.Errors.Select(e => new { e.Key, e.Code, e.Message }) },
        }, statusCode: context.StatusCode);
        return ValueTask.CompletedTask;
    }
}
```

Both methods are optional, and `AddFilter(onRequest: …, onValidationFailed: …)` registers one inline. `context.Result` starts
with the default problem response, so a filter that only logs leaves it alone. Custom validators can report codes too:
`context.AddError(path, message, code)`.

The routed endpoint carries the complete definition, so your own middleware doesn't need the store either:
`HttpContext.GetDynamicEndpoint()` gives `Definition`, `ProcessorName`, `Parameters`, `FindParameter`, `HasFiles` and friends.
</details>

<details>
<summary><b>Handing work on: validators → processor</b></summary>

A validator that already parsed a value, or loaded an entity, hands it on instead of letting the processor repeat the work:

```csharp
// in a parameter validator
var document = Decode(context.GetValue<string>());
context.SetParsedValue(document);               // for the validated parameter (or pass a parameter name)
context.Items["customer"] = customer;           // anything else, shared by filters, validators and the processor

// in the processor
var document = request.GetParsedValue<Document>("document");
var customer = (Customer)request.Items["customer"]!;
```
</details>

<details>
<summary><b>One error format: <code>IDynamicErrorResponseFactory</code></b></summary>

One factory builds every error: validation errors, malformed bodies, 413 and 415. With the middleware it also covers empty 401/403
responses from authentication and authorization, 404 for unknown routes, 405, empty errors returned by processors, and unhandled
exceptions. So `UseStatusCodePages` isn't needed any more.

```csharp
builder.Services.AddDynamicEndpoints().UseErrorResponses(e => Results.Json(new
{
    apiVersion = "1.0",
    error = new { code = e.Kind.ToString(), message = e.Title,                 // Validation, NotFound, Unauthorized, Exception, …
                  details = e.Errors.Select(x => new { x.Key, x.Code, x.Message }) },
    requestId = e.RequestId,                                                     // trace id
}, statusCode: e.StatusCode));                                                   // or UseErrorResponseFactory<MyFactory>()

app.UseDynamicEndpointsErrorResponses(o => o.AppliesTo = c => c.Request.Path.StartsWithSegments("/api"));   // early in the pipeline
```

- Responses that already have a body are left alone, and `[SkipStatusCodePages]` is respected.
- Exceptions are logged and answered with a 500. `e.Exception` is there for you, but never in the default response. To let
  `UseExceptionHandler` handle them instead, set `o.HandleExceptions = false`.
- Titles are localized like the validation messages. `e.Endpoint` is the dynamic endpoint (`null` for unknown routes).
- `IDynamicEndpointFilter.OnValidationFailedAsync` still runs afterwards and can replace the result of a single request.
</details>

<details>
<summary><b>Localized error messages</b></summary>

English and Polish are built in, with proper plural forms ("2 znaki", "5 znaków").

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.Messages.DefaultCulture = "pl";                                    // always Polish…
    o.Messages.UseRequestCulture = true;                                 // …or per request (app.UseRequestLocalization())
    o.Messages.Set("pl", "required.header", "Brak nagłówka {0}.");       // override a single text
    o.Messages.Set("de", "minLength", "Mindestens {0} Zeichen.");        // add a language
    o.Messages.Localizer = c => localizer[c.Key, c.Arguments.ToArray()]; // or route everything through IStringLocalizer
});
```

Keys follow the error codes (`minLength`, `format.email`, `required.query`, `type.integer`, `file.maxSize`, …), and
`DynamicValidationMessages.Keys` lists them all. Rule messages are whatever the admin wrote.
</details>

<details>
<summary><b>OpenAPI: security schemes, common headers, examples, hooks</b></summary>

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.OpenApi.AddApiKey("X-Api-Key");                                    // securitySchemes + a requirement on every non-anonymous operation
    o.OpenApi.AddSecurityScheme("Bearer", bearerScheme, appliesTo: d => d.Group == "partners");
    o.OpenApi.AddHeader("X-End-User", "End user the call is made for.", required: true);
    o.OpenApi.AddHeader("X-Seat-Id", "Seat of the end user.");
    o.OpenApi.AddHeader("Idempotency-Key", "Makes retries safe.", appliesTo: d => d.Method != "GET",
        schema: new JsonObject { ["type"] = "string", ["format"] = "uuid" }, example: "6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    o.OpenApi.ConfigureOperation = (operation, definition) => { /* x-extensions, extra responses */ };
    o.OpenApi.ConfigureDocument = document => { /* servers, your error schema */ };
});

DynamicEndpoint.Post("/orders")
    .FromBody("sku", p => p.Required().Example("A-1"))       // examples compose the request example…
    .WithRequestExample(new { sku = "A-1", quantity = 2 })   // …or set it by hand
    .WithResponseExample(new { id = 7, status = "accepted" });
```

- **Headers** are real header parameters of each operation, so client generators produce arguments for them.
- **Security requirements** are attached to each operation. Endpoints with `AllowAnonymous` get none.
- **Examples:** parameter examples show up on query, header and route parameters and in the request body example.
</details>

<details>
<summary><b>Options</b></summary>

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.ReservedPrefixes.Add("/internal");          // never usable by dynamic endpoints
    o.RequiredRoutePrefixes.Add("/api/v{version:int}");  // every route must start with /api/v1, /api/v2, …
    o.DefaultProcessor = "orders";                // when a definition names none
    o.MaxRequestBodySize = 1024 * 1024;           // JSON bodies, bytes
    o.MaxFormBodySize = 30 * 1024 * 1024;         // form bodies with all their files, bytes
    o.MaxJsonDepth = 32;
    o.RefreshInterval = TimeSpan.FromSeconds(30); // multi-instance polling (the fallback when a change notifier is used)
    o.InstanceId = "api-1";                       // identifies this instance in change notifications (unique by default)
    o.ThrowOnStartupLoadFailure = true;
    o.ConfigureEndpoint = (builder, definition) => { /* extra metadata */ };
    o.OpenApi.Title = "My dynamic API";
    o.OpenApi.DefaultGroup = "Dynamic";
});

app.MapDynamicEndpoints().RequireRateLimiting("api");   // conventions for all dynamic endpoints
```
</details>

<details>
<summary><b>Multiple instances</b></summary>

Each instance keeps its own routing table. A change notifier tells the others right away, and polling catches whatever a
notifier missed:

```csharp
builder.Services.AddDynamicEndpoints(o => o.RefreshInterval = TimeSpan.FromMinutes(5))   // fallback only
    .UseEntityFrameworkStore<AppDbContext>()
    .UsePostgreSqlChangeNotifications(connectionString);    // DynamicEndpoints.PostgreSql: LISTEN/NOTIFY, no extra infrastructure
 // .UseRedisChangeNotifications("redis:6379");             // DynamicEndpoints.Redis: pub/sub
 // .UseChangeNotifier<MyServiceBusNotifier>();             // or your own IDynamicEndpointChangeNotifier
```

- **Instant:** every change is published after it was applied. The other instances reload at once, and bursts are coalesced.
- **Resilient:** listeners reconnect on their own and reload after every reconnect, in case something was missed while
  disconnected. An instance ignores its own messages.
- **Push by hand:** `IDynamicEndpointManager.ReloadAsync()` still works from any signal of yours.
- **Visibility:** the admin list reports `Pending` for changes this instance hasn't picked up yet.
- **Conflicting writes:** a stale write from another instance is rejected by the database concurrency token.
</details>

<details>
<summary><b>Change events: audit, cache invalidation</b></summary>

```csharp
builder.Services.AddDynamicEndpoints()
    .AddChangeHandler<AuditChangeHandler>()                   // scoped, run in registration order
    .OnChanged((change, ct) => cache.RemoveAsync(change.Id.ToString(), ct));

public sealed class AuditChangeHandler(AuditLog audit) : IDynamicEndpointChangeHandler
{
    public Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken ct) =>
        change.Origin == DynamicEndpointChangeOrigin.Local     // once, on the instance that made the change
            ? audit.WriteAsync(change.Kind, change.Id, change.Previous, change.Definition, ct)
            : Task.CompletedTask;
}
```

- `Kind` is `Created`, `Updated` (including enable/disable) or `Deleted`. `Previous` and `Definition` are the definitions before
  and after the change.
- `Origin` is `Local` for changes made through this instance's manager, raised once. It's `Remote` for changes picked up by a
  reload, raised on every other instance, which is what per-instance caches need.
- Handlers run after the routing table was updated. Exceptions are logged and don't undo the change.
</details>

<details>
<summary><b>Metrics &amp; tracing: OpenTelemetry</b></summary>

The core emits metrics through a `Meter` and spans through an `ActivitySource`, both named `DynamicEndpoints`. There's no
OpenTelemetry dependency: wire them up with the `DynamicEndpoints.OpenTelemetry` package…

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddDynamicEndpointsInstrumentation())
    .UseOtlpExporter();
```

…or with the constants: `m.AddMeter(DynamicEndpointsTelemetry.MeterName)`, `t.AddSource(DynamicEndpointsTelemetry.ActivitySourceName)`.
`dotnet-counters monitor --counters DynamicEndpoints` works without any setup.

| Metric | Type | Extra tags |
|---|---|---|
| `dynamic_endpoints.requests` | counter | `dynamic_endpoint.outcome` (`processed`, `rejected`, `short_circuited`, `error`), `http.response.status_code` |
| `dynamic_endpoints.request.duration` | histogram (s) | same as above |
| `dynamic_endpoints.validation.failures` | counter | `dynamic_endpoint.validation.layer`: `binding`, `constraints`, `parameter_validators`, `rules`, `request_validators` |
| `dynamic_endpoints.processor.duration` | histogram (s) | |
| `dynamic_endpoints.errors` | counter | `error.type` |

- **Per endpoint:** every measurement carries `dynamic_endpoint.id`, `dynamic_endpoint.name`, `dynamic_endpoint.processor`,
  `http.route` and `http.request.method`. ASP.NET Core's own `http.server.request.duration` gets the dynamic route template too.
- **Spans:** `DynamicEndpoints.Request` with the children `Filters`, `Binding`, `Validation.{layer}` and `Processor`, below the
  ASP.NET Core request span. Failed layers and exceptions set the span status to `Error`.
- **Cheap when unused:** instruments are only fed when something listens, and spans only exist for sampled requests.
- All names are constants in `DynamicEndpointsTelemetry` (`Instruments`, `Activities`, `Tags`, `Outcomes`, `ValidationLayers`).
</details>

<details>
<summary><b>EF Core: migrations</b></summary>

**Your own DbContext** (recommended): add the table to your model, and your migrations create and evolve it.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyDynamicEndpointsConfiguration();   // or ApplyConfiguration(new DynamicEndpointRecordConfiguration(table, schema))
```

```bash
dotnet ef migrations add AddDynamicEndpoints
```

`ApplyDynamicEndpointsConfiguration()` maps three tables: `DynamicEndpoints`, plus `DynamicEndpointRevisions` and
`DynamicEndpointDrafts` for history and drafts. Upgrading from 0.3, add a migration for the two new ones, or pass
`history: false` to keep the old model (and no history and drafts).

**The bundled `DynamicEndpointsDbContext`** ships its own provider-independent migrations:

```csharp
builder.Services.AddDynamicEndpoints()
    .UseEntityFrameworkStore(o => o.UseNpgsql(connectionString), migrateOnStartup: true);
// or apply them in your deployment step: await db.Database.MigrateAsync();
```

A table created earlier with `EnsureCreated` is adopted into the migration history on the first `migrateOnStartup`.
Definitions that existed before the history tables start their history with their published revision. With your own
context, add the table to an empty initial migration the usual EF Core way. `MigrateOnStartup<TContext>()` applies your own
context's migrations on start-up, and any `IDynamicEndpointStoreInitializer` runs before definitions are loaded.
</details>

<details>
<summary><b>Testing: <code>DynamicEndpoints.Testing</code></b></summary>

```csharp
// Your application, without its database: in-memory store, no migrations, no notifier, no polling.
await using var factory = new WebApplicationFactory<Program>()
    .WithInMemoryDynamicEndpoints(b => b.AddProcessor("fake-crm", _ => Results.Ok(new { id = 1 })));
await factory.AddDynamicEndpointAsync(DynamicEndpoint.Get("/customers/{id}").HandledBy("fake-crm").FromRoute("id"));
var response = await factory.CreateClient().GetAsync("/customers/1");

// Or just your processors and validators, without the application.
await using var server = await DynamicEndpointsTestServer.StartAsync(b => b.AddProcessor<OrderLookupProcessor>());
await server.AddEndpointAsync(DynamicEndpoint.Get("/orders/{id}").HandledBy<OrderLookupProcessor>().FromRoute("id"));
```

`services.UseInMemoryDynamicEndpoints()` does the same in your own `ConfigureTestServices`. To test several instances, share one
`InMemoryDynamicEndpointStore` and one `InMemoryDynamicEndpointChangeNotifier` between servers.
</details>

<details>
<summary><b>Limitations (deliberate)</b></summary>

- `MapDynamicEndpoints()` must be called on the application, not inside a `MapGroup`.
- Conflict detection compares route *shapes*. It doesn't analyse constraints or optional segments.
- Query arrays use repeated keys (`?tag=a&tag=b`). Header arrays are comma separated.
- `DateTime` values must be RFC 3339 with an offset (`2026-01-31T12:00:00Z`).
</details>

## 🔒 Security

- 🚫 **Nothing is executed from definitions.** JsonLogic is a closed set of operators. There's no scripting and no Roslyn.
- 🧨 **ReDoS-proof:** regex constraints run on `RegexOptions.NonBacktracking`. Backreferences and lookarounds are rejected on save.
- 📏 **Limits:** JSON body size (1 MB), form body size (30 MB) and JSON depth (32) by default. Bodies that aren't valid UTF-8 are rejected.
- 🧱 **Reserved prefixes:** the admin API is protected automatically, the rest via options. Clashes with the app's own endpoints are rejected.
- 🔑 **Policies:** authorization policies referenced by definitions must exist when the definition is saved.
- ⚠️ **The admin API is open by default.** Put `.RequireAuthorization(...)` on it.

## 🖼️ Screenshots

| Endpoint editor | Swagger UI (OpenAPI 3.1, generated live) |
|---|---|
| <img src="docs/images/editor.jpg" alt="Endpoint editor" width="420"> | <img src="docs/images/swagger.jpg" alt="Swagger UI" width="420"> |

## 📦 Packages

| Package | What |
|---|---|
| `DynamicEndpoints` | core: routing, binding, validation engines, manager, admin API, OpenAPI. **Zero third-party dependencies** |
| `DynamicEndpoints.AdminUI` | the admin panel, `MapDynamicEndpointsAdminUI()` |
| `DynamicEndpoints.EntityFrameworkCore` | persistence with EF Core |
| `DynamicEndpoints.FluentValidation` | FluentValidation validators as dynamic validators |
| `DynamicEndpoints.PostgreSql` | instant multi-instance propagation through `LISTEN/NOTIFY` |
| `DynamicEndpoints.Redis` | instant multi-instance propagation through Redis pub/sub |
| `DynamicEndpoints.OpenTelemetry` | `AddDynamicEndpointsInstrumentation()` for OpenTelemetry metrics and tracing |
| `DynamicEndpoints.Testing` | in-memory store for `WebApplicationFactory`, test server, in-memory notifier |

## 🧪 Sample app

```bash
dotnet run --project samples/DynamicEndpoints.Sample
```

- 🖥️ **Admin panel:** `http://localhost:5118/admin/`, from the `DynamicEndpoints.AdminUI` package. List, editor (parameters, constraints, rules,
  validators, processor config), drafts and scheduled publishing, history with diffs and rollback, and a "Try" console with
  generated examples and curl / HTTPie / C# snippets.
- 📜 **Swagger UI:** `http://localhost:5118/swagger`, with the *Dynamic endpoints* and *Admin API* documents.
- 🧩 **Processors:** `echo`, `template`, `calculator`, `collection` (a JSON document store in SQLite) and an inline `clock`.
- 🛡️ **Validators:** `nip` (C#), `unique-value` (C#, DB lookup), `iban` and `booking-request` (FluentValidation). `POST /contacts` shows the built-in formats.
- 🌱 **Seeding:** `SampleEndpointsSeeder` seeds the demo endpoints on the first start.
- 👋 **Custom management API:** `Greetings/` builds its own API on the injected `IDynamicEndpointManager`.

## 🚢 Releasing

**Every push to `main` is a release.** `.github/workflows/release.yml` builds, tests, packs and publishes to NuGet through
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) (no API key in the repo), then tags the commit and
creates a GitHub Release with generated notes.

| Version part | Comes from |
|---|---|
| `major.minor` | `<VersionPrefix>` in `Directory.Build.props`. Bump it by hand |
| `patch` | the last `vX.Y.*` tag + 1. Automatic, gap-free, resets after a prefix bump |

Pushes that only touch `README.md`, `docs/` or `samples/` are not released.

Release notes come from [`CHANGELOG.md`](CHANGELOG.md). Add entries under **[Unreleased]** together with your change, and the
workflow moves them under the released version. Pushes without entries get auto-generated notes only, and no bot commit.
After a release with entries, run `git pull` before your next commit (the workflow committed the changelog).

## 🛠️ Building & testing

```bash
dotnet build DynamicEndpoints.slnx
dotnet test DynamicEndpoints.slnx
```

```
src/DynamicEndpoints                       core library
src/DynamicEndpoints.AdminUI               admin panel (embedded HTML/JS)
src/DynamicEndpoints.EntityFrameworkCore   EF Core store
src/DynamicEndpoints.FluentValidation      FluentValidation integration
src/DynamicEndpoints.PostgreSql            LISTEN/NOTIFY change notifier
src/DynamicEndpoints.Redis                 Redis pub/sub change notifier
src/DynamicEndpoints.OpenTelemetry         OpenTelemetry registration
src/DynamicEndpoints.Testing               test helpers
samples/DynamicEndpoints.Sample            demo app: admin panel, Swagger UI, SQLite
tests/DynamicEndpoints.Tests               integration tests (TestServer + SQLite; PostgreSQL and Redis in Docker)
```

Tests marked `[DockerFact]` start PostgreSQL and Redis containers (Testcontainers) and are skipped when Docker isn't available.

## 🗺️ Roadmap

- [x] Runtime endpoints with persistence and multi-instance refresh
- [x] JSON Schema constraints, JsonLogic rules, custom & FluentValidation validators
- [x] OpenAPI 3.1 generation
- [x] Assembly scanning & seeding
- [x] Zero-dependency core (built-in JSON Schema subset & JsonLogic engine)
- [x] Built-in string formats
- [x] Continuous delivery: every push to `main` publishes a new NuGet version
- [x] Draft → publish workflow with version history, diffs, rollback and scheduled publishing
- [x] Instant change propagation: PostgreSQL `LISTEN/NOTIFY` and Redis pub/sub
- [x] Change events, transactional change sets, EF Core migrations, test kit
- [x] Admin UI as a reusable package
- [x] OpenTelemetry metrics and tracing per dynamic endpoint

## ⚖️ License

DynamicEndpoints is licensed under the [MIT License](LICENSE). Use it in commercial projects, no strings attached.

| Package | Depends on |
|---|---|
| `DynamicEndpoints` | ASP.NET Core shared framework only |
| `DynamicEndpoints.AdminUI` | ASP.NET Core shared framework only |
| `DynamicEndpoints.EntityFrameworkCore` | `Microsoft.EntityFrameworkCore.Relational` (MIT) |
| `DynamicEndpoints.FluentValidation` | [FluentValidation](https://github.com/FluentValidation/FluentValidation) (Apache-2.0) |
| `DynamicEndpoints.PostgreSql` | [Npgsql](https://github.com/npgsql/npgsql) (PostgreSQL License) |
| `DynamicEndpoints.Redis` | [StackExchange.Redis](https://github.com/StackExchange/StackExchange.Redis) (MIT) |
| `DynamicEndpoints.OpenTelemetry` | [OpenTelemetry.Api](https://github.com/open-telemetry/opentelemetry-dotnet) (Apache-2.0) |
| `DynamicEndpoints.Testing` | `Microsoft.AspNetCore.Mvc.Testing` (MIT) |

## 🤝 Contributing

Issues and PRs are welcome. Run `dotnet test` before you push, and keep changes covered by integration tests.

<div align="center">

---

Made with ☕ and a healthy disrespect for recompiling.

**If this saved you a sprint, drop a ⭐**

</div>
