# 02 – Validation

Four layers of validation, each with its own endpoint, all answered by an `echo` processor that returns the validated
parameters – so a request shows either what a processor would get, or the errors. Plus stable error codes, one error format
for every error (`IDynamicErrorResponseFactory`) and messages in English or Polish.

| Endpoint | Layer | What to look at |
|---|---|---|
| `POST /contacts` | constraints (JSON Schema), defined by admins | required, lengths, allowed values, array limits and the built-in formats e-mail, phone, URI, time |
| `POST /invoices` | parameter validators, attached by admins | [`NipValidator`](Validators/NipValidator.cs) (C#, with codes `nip.length` / `nip.checksum`) and `IbanValidator` (FluentValidation) |
| `POST /bookings` | rules (JsonLogic) + a request validator | check-out after check-in (code `stayOrder`), "single room = 1 guest", and [`BookingRequestValidator`](Validators/FluentValidators.cs) (FluentValidation, the whole request) |
| `POST /orders` | request validator in C# with configuration | [`OrderTotalValidator`](Validators/OrderTotalValidator.cs): quantity × unit price ≤ `maxTotal` (5000 here), code `orderTotal` |

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `AddFluentValidatorsFromAssemblyContaining<Program>()`, `options.Messages` (request culture, overridden texts), `UseErrorResponseFactory<…>()`, `UseDynamicEndpointsErrorResponses()`, `UseRequestLocalization()` |
| [`ValidationSeeder.cs`](ValidationSeeder.cs) | the four endpoints, built with the fluent API |
| [`ApiErrorResponseFactory.cs`](ApiErrorResponseFactory.cs) | the application's own error format: `{ "error": { "code", "message", "details": [{ "field", "code", "message" }] }, "requestId" }` |
| [`validation.http`](validation.http) | the requests below |

## Run it

```bash
dotnet run --project samples/02-Validation
```

Definitions in `validation.db` (in the sample's folder, the working directory of `dotnet run`). The panel is at <http://localhost:5102/admin/>, Swagger UI at
<http://localhost:5102/swagger>.

## Click around

- In the panel, open *Validate contact* and use the *Try* console: clear the e-mail, type `nope` – all errors come back at once.
- Open *Validate invoice*: the parameters `nip` and `iban` have their validators attached in the *Validators* section of the
  parameter. The panel only offers validators that fit the parameter type.
- Open *Validate booking*: the *Rules* section holds the JsonLogic conditions with their messages, parameters and codes; the
  request validator `booking-request` is attached at the bottom.
- Change `maxTotal` of *Validate order* in its validator configuration, save, and try `quantity` 1000 again.

## Call it

```bash
# 400 – all constraint errors at once, in the application's error format
curl -X POST http://localhost:5102/contacts -H "Content-Type: application/json" -d '{"email":"not-an-email","phone":"123","country":"FR"}'
# {"error":{"code":"Validation","message":"One or more validation errors occurred.",
#   "details":[{"field":"name","code":"required","message":"The 'name' field is required."},
#              {"field":"email","code":"format","message":"Must be a valid e-mail address."}, …]},"requestId":"…"}

# The same in Polish
curl -X POST http://localhost:5102/contacts -H "Accept-Language: pl" -H "Content-Type: application/json" -d '{"name":"Jan","email":"nope"}'
# … "message":"Musi być poprawnym adresem e-mail." …

# Valid: the normalized parameters, the default country filled in
curl -X POST http://localhost:5102/contacts -H "Content-Type: application/json" -d '{"name":"Jan Kowalski","email":"jan@example.com"}'

# Parameter validators: a wrong NIP checksum (code nip.checksum) and an invalid IBAN
curl -X POST http://localhost:5102/invoices -H "Content-Type: application/json" -d '{"nip":"526-000-12-47","iban":"PL00 1234","amount":10}'

# Rules: check-out before check-in (code stayOrder), two guests in a single room (code rule)
curl -X POST http://localhost:5102/bookings -H "Content-Type: application/json" -d '{"roomType":"single","guests":2,"checkIn":"2030-11-05","checkOut":"2030-11-01"}'

# Request validator (FluentValidation): more than 14 nights
curl -X POST http://localhost:5102/bookings -H "Content-Type: application/json" -d '{"roomType":"double","guests":2,"checkIn":"2030-11-01","checkOut":"2030-11-30"}'

# Request validator (C#, configured per endpoint): 1000 × 49.90 is above 5000 – code orderTotal
curl -X POST http://localhost:5102/orders -H "Content-Type: application/json" -d '{"sku":"ANV-1","quantity":1000,"unitPrice":49.9}'

# The error factory also answers unknown routes and broken bodies
curl http://localhost:5102/nothing-here                               # {"error":{"code":"NotFound", …}}
curl -X POST http://localhost:5102/contacts -H "Content-Type: application/json" -d '{ nope'   # {"error":{"code":"InvalidBody", …}}
```

## Read more

- [Validation: four layers](../../docs/articles/validation.md) · [One error format](../../docs/articles/error-responses.md) ·
  [Localized error messages](../../docs/articles/localization.md)
- [Processors, validators & seeders](../../docs/articles/processors-and-validators.md) · [Filters](../../docs/articles/filters.md)
- Next: [03 – Custom processors](../03-CustomProcessors/README.md), or the [overview of all samples](../../docs/articles/samples.md).
