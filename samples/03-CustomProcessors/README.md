# 03 – Custom processors

Processors are where your code runs: they get the validated parameters and the endpoint's typed configuration, and they are
ordinary DI services. This sample has several, plus the things around them – a filter, forms and file uploads, a validator
that hands its work on, and a management API of your own on `IDynamicEndpointManager`.

| File | What to look at |
|---|---|
| [`Processors/TemplateProcessor.cs`](Processors/TemplateProcessor.cs) | `DynamicEndpointProcessor<TemplateConfig>`: a typed configuration with DataAnnotations, checked when the definition is saved |
| [`Processors/CalculatorProcessor.cs`](Processors/CalculatorProcessor.cs) | one processor, several endpoints: the configuration picks the operation (`/calc/sum`, `/calc/average`) |
| [`Processors/CollectionProcessor.cs`](Processors/CollectionProcessor.cs) | DI and database access: `AppDbContext` and `TimeProvider` injected, a JSON document store in SQLite |
| [`Processors/FileInfoProcessor.cs`](Processors/FileInfoProcessor.cs) | `request.GetFile("file")` – a streamed `IFormFile`, size and content type checked by the definition |
| [`Validators/CsvValidator.cs`](Validators/CsvValidator.cs) + [`Processors/CsvSummaryProcessor.cs`](Processors/CsvSummaryProcessor.cs) | handing work on: `context.SetParsedValue(rows)` in the validator, `request.GetParsedValue<T>("csv")` in the processor, `Items` for the rest |
| [`ApiKeyFilter.cs`](ApiKeyFilter.cs) | an `IDynamicEndpointFilter`: every `DELETE` endpoint needs `X-Api-Key`; rejected requests are logged and get an `X-Validation-Errors` header |
| [`Greetings/`](Greetings/) | a purpose-built API on the injected `IDynamicEndpointManager`: `POST /api/greetings` publishes `GET /greetings/{slug}/{name}` |
| [`Program.cs`](Program.cs) | `AddFromAssemblyContaining<Program>()`, an inline `AddProcessor("clock", …)`, `AddFilter<ApiKeyFilter>()` |
| [`CustomProcessorsSeeder.cs`](CustomProcessorsSeeder.cs) | `HandledBy<TProcessor, TConfig>(…)` and `ValidatedBy<TValidator>(config)` – typed, no magic strings |
| [`custom-processors.http`](custom-processors.http) | the requests below |

## Run it

```bash
dotnet run --project samples/03-CustomProcessors
```

Definitions and notes in `custom-processors.db` (in the sample's folder, the working directory of `dotnet run`). Panel: <http://localhost:5103/admin/>, Swagger UI:
<http://localhost:5103/swagger> (the *Admin API and greetings API* document has the custom API).

## Click around

- In the panel, create an endpoint with the `template` processor: the *Configuration example* button fills in the JSON, and a
  configuration without `template` is rejected on save – that's the `[Required]` of `TemplateConfig`.
- Open *Upload document* and use the *Try* console: it shows a file picker, and a `.txt` file is rejected with `fileType`.
- In Swagger UI, `POST /api/greetings` with `{ "slug": "pirate", "greeting": "Ahoy" }`, then reload the panel: the endpoint is there,
  in the group *Greetings*.

## Call it

```bash
curl http://localhost:5103/hello/Ada                                   # Hello, Ada! …
curl "http://localhost:5103/calc/sum?a=2&b=40"                         # {"operation":"Sum","values":[2,40],"result":42}
curl "http://localhost:5103/calc/average?values=1&values=2&values=6"   # result 3
curl "http://localhost:5103/time?timeZone=Europe/Warsaw"

# Notes in SQLite; deleting needs the API key (ApiKeyFilter, Samples:ApiKey in appsettings.json)
curl -X POST http://localhost:5103/notes -H "Content-Type: application/json" -d '{"title":"Buy milk"}'   # 201 with the id
curl http://localhost:5103/notes
curl -X DELETE http://localhost:5103/notes/<id>                                                  # 401
curl -X DELETE http://localhost:5103/notes/<id> -H "X-Api-Key: sample-api-key"                   # 204

# A rejected request: the default 400, plus the header the filter added
curl -i -X POST http://localhost:5103/notes -H "Content-Type: application/json" -d '{"title":"x"}'   # X-Validation-Errors: 1

# File upload (multipart/form-data)
curl -F title=Contract -F "file=@contract.pdf;type=application/pdf" http://localhost:5103/documents

# The validator parses the CSV, the processor gets the rows
curl -X POST http://localhost:5103/imports/csv -H "Content-Type: application/json" -d '{"csv":"sku,quantity\nANV-1,2\nROC-2,1"}'

# A management API of your own
curl -X POST http://localhost:5103/api/greetings -H "Content-Type: application/json" -d '{"slug":"pirate","greeting":"Ahoy"}'
curl http://localhost:5103/greetings/pirate/Jack                       # Ahoy, Jack!
curl -X DELETE http://localhost:5103/api/greetings/pirate
```

## Read more

- [Processors, validators, seeders & assembly scanning](../../docs/articles/processors-and-validators.md) ·
  [Filters](../../docs/articles/filters.md) · [File uploads & forms](../../docs/articles/file-uploads.md) ·
  [Handing work on](../../docs/articles/handing-work-on.md)
- [`IDynamicEndpointManager`](../../docs/articles/managing-endpoints.md) · [Change sets](../../docs/articles/change-sets.md) ·
  [Testing](../../docs/articles/testing.md)
- Next: [04 – Built-in processors](../04-BuiltInProcessors/README.md), or the [overview of all samples](../../docs/articles/samples.md).
