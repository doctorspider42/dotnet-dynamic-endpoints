# 07 – Import from OpenAPI

Got a contract first? [`petstore.json`](petstore.json) becomes runtime endpoints: in **mock mode** every operation answers with
its documented example (rendered with the request's values – `{{petId}}`), the parameters and bodies are validated as the
document says, and one operation asks for a real processor by name. [`petstore-v2.json`](petstore-v2.json) is the next version
of the contract, for a **re-import with sync**. It all happens twice: from code on start, and by hand in the panel's wizard.

| File | What to look at |
|---|---|
| [`PetstoreImport.cs`](PetstoreImport.cs) | the import from code: `IDynamicEndpointOpenApiImporter.ImportAsync(document, new() { Mock = true, Mode = Sync, Enabled = true })`, on every start |
| [`petstore.json`](petstore.json) | the contract: examples, enums, limits, a `$ref`, and `x-dynamic-endpoints-processor: inventory` on `GET /store/inventory` |
| [`petstore-v2.json`](petstore-v2.json) | `listPets` gets a `species` filter, `deletePet` is gone, `listPhotos` is new |
| [`Program.cs`](Program.cs) | `AddResponseTemplateProcessor()` (mock mode needs it), the `inventory` and `echo` processors, `AddYamlFormat()` |
| [`openapi-import.http`](openapi-import.http) | the requests below |

## Run it

```bash
dotnet run --project samples/07-OpenApiImport
```

Definitions in `openapi-import.db` (in the sample's folder, the working directory of `dotnet run`). Panel:
<http://localhost:5107/admin/>, Swagger UI: <http://localhost:5107/swagger>. The log says what the start-up import did
(`Imported Petstore: 6 created, …` the first time, `… 6 unchanged …` after that).

`petstore.json` is the source of truth: the start-up sync creates, updates and deletes the endpoints imported from it, and never
touches endpoints you made by hand. Edit the file and restart to see it. (A version 2 imported by hand is reverted by the next
start – the file wins.)

## Click around

- **OpenAPI import** (toolbar above the list): paste the content of <http://localhost:5107/specs/petstore-v2.json>, mode `sync`,
  check *Mock* and *Enabled*, *Dry run*: `listPets` *update* (`parameters`), `listPhotos` *create*, `deletePet` *delete*, the rest
  *unchanged*. Then *Import*.
- The same document, not as a mock: mode `create`, route prefix `/v1`, processor `echo`, and in the tag table `store` → `echo`.
  The dry run shows for every operation which processor it gets and why (`Tag`, `Option`, `OperationExtension`).
- Open an imported endpoint: its *origin* records the document and the operation, so a re-import finds it again.

## Call it

```bash
curl http://localhost:5107/pets                         # the example of listPets
curl http://localhost:5107/pets/7                       # {"id":7,"name":"Rex",…} – {{petId}} rendered
curl -X POST http://localhost:5107/pets -H "Content-Type: application/json" -d '{"name":"Polly","species":"bird"}'   # 201
curl -X POST http://localhost:5107/pets -H "Content-Type: application/json" -d '{"name":"Nemo","species":"fish"}'    # 400 – enum
curl http://localhost:5107/store/inventory              # the "inventory" processor – x-dynamic-endpoints-processor

# Re-import version 2: the dry run first, then for real
curl -X POST "http://localhost:5107/api/admin/endpoints/import/openapi?mock=true&mode=sync&enabled=true&dryRun=true" \
  -H "Content-Type: application/json" --data-binary @samples/07-OpenApiImport/petstore-v2.json
curl -X POST "http://localhost:5107/api/admin/endpoints/import/openapi?mock=true&mode=sync&enabled=true" \
  -H "Content-Type: application/json" --data-binary @samples/07-OpenApiImport/petstore-v2.json
curl http://localhost:5107/pets/1/photos                # new in version 2

# A processor per tag (not a mock), under a route prefix – dry run
curl -X POST "http://localhost:5107/api/admin/endpoints/import/openapi?processor=echo&processorByTag=store:echo&routePrefix=/v1&dryRun=true" \
  -H "Content-Type: application/json" --data-binary @samples/07-OpenApiImport/petstore.json

# The same with the CLI of this repository (see 08-GitOps)
dotnet run --project src/DynamicEndpoints.Cli -- import-openapi samples/07-OpenApiImport/petstore-v2.json \
  --url http://localhost:5107/api/admin/endpoints --mock --mode sync --enabled --dry-run
```

## Read more

- [Import from OpenAPI](../../docs/articles/openapi-import.md) – the mapping, options, a processor per operation, mock mode,
  re-importing
- [OpenAPI](../../docs/articles/openapi.md) · [Export, import & GitOps](../../docs/articles/export-import-gitops.md) (the CLI) ·
  [Admin panel](../../docs/articles/admin-ui.md) (the wizard)
- Next: [08 – GitOps](../08-GitOps/README.md), or the [overview of all samples](../../docs/articles/samples.md).
