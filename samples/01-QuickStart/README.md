# 01 – Quick start

The minimum: a SQLite store in your own `DbContext`, the admin REST API and panel, Swagger UI, one typed processor and a
seeder. It is the [README quick start](../../README.md#-quick-start) and what `dotnet new dynamic-endpoints` generates
([project template](../../docs/articles/project-template.md)), with nothing else around it.

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | the whole setup, step by step: `AddDynamicEndpoints()`, `UseEntityFrameworkStore<AppDbContext>()`, `MapDynamicEndpoints()`, the admin API, the panel and OpenAPI |
| [`AppDbContext.cs`](AppDbContext.cs) | `ApplyDynamicEndpointsConfiguration()` adds the definition tables to your model |
| [`GreetingProcessor.cs`](GreetingProcessor.cs) | a typed processor: `DynamicEndpointProcessor<GreetingConfig>`, the configuration is per endpoint |
| [`QuickStartSeeder.cs`](QuickStartSeeder.cs) | two endpoints built with the fluent API, seeded into an empty store |
| [`quickstart.http`](quickstart.http) | the requests below, for VS / Rider / VS Code REST Client |

## Run it

```bash
dotnet run --project samples/01-QuickStart
```

The definitions are stored in `quickstart.db` in the directory you started it from. Delete the file to start over.

## Click around

- **Admin panel:** <http://localhost:5101/admin/>. Open *Say hello*, change the length limits or the greeting in the processor
  configuration, *Save & publish*, and call it again in the *Try* console. Or create a new endpoint with the `greeting` processor.
- **Swagger UI:** <http://localhost:5101/swagger>, with the *Dynamic endpoints* document (generated from the definitions) and the
  *Admin API*.

## Call it

```bash
curl http://localhost:5101/hello/Ada                 # {"message":"Hello, Ada!", …}
curl http://localhost:5101/hello/A                   # 400: name is too short (2–30 characters)
curl http://localhost:5101/greetings?name=Ada        # {"message":"GOOD MORNING, ADA!", …}
curl http://localhost:5101/api/admin/endpoints       # the definitions with their status

# A new endpoint through the admin API – it answers right away, and after a restart too.
curl -X POST http://localhost:5101/api/admin/endpoints -H "Content-Type: application/json" -d '{
  "method": "GET", "route": "/ahoy", "name": "Ahoy",
  "processor": "greeting", "processorConfig": { "greeting": "Ahoy" },
  "parameters": [{ "name": "name", "source": "Query", "maxLength": 30 }]
}'
curl "http://localhost:5101/ahoy?name=Jack"          # {"message":"Ahoy, Jack!", …}
```

## Read more

- [Getting started](../../docs/articles/getting-started.md) · [How it works](../../docs/articles/how-it-works.md)
- [Processors, validators & seeders](../../docs/articles/processors-and-validators.md) · [Definition model](../../docs/articles/definition-model.md)
- [Admin REST API](../../docs/articles/admin-api.md) · [Admin panel](../../docs/articles/admin-ui.md) · [EF Core & migrations](../../docs/articles/ef-core.md)
- Next: [02 – Validation](../02-Validation/README.md), or the [overview of all samples](../../docs/articles/samples.md).
