# Import from OpenAPI

Got a contract first? Turn an OpenAPI 3.x document into endpoint skeletons, give each operation its processor, serve
the documented examples as a mock, and re-import the document when it changes:

```bash
curl -X POST "https://api.example.com/api/admin/endpoints/import/openapi?processor=http-forward&routePrefix=/partners&dryRun=true" \
  -H "Content-Type: application/json" --data-binary @partner-api.json      # YAML with DynamicEndpoints.Yaml
```

```csharp
var result = await importer.ImportAsync(document, new OpenApiImportOptions { Processor = "orders", DryRun = true });   // IDynamicEndpointOpenApiImporter
```

…or with the CLI: `dynamic-endpoints import-openapi partner-api.yaml --processor http-forward --route-prefix /partners --dry-run`
([GitOps](export-import-gitops.md#the-dynamic-endpoints-cli)), or with the [admin panel's wizard](admin-ui.md).

| OpenAPI | Definition |
|---|---|
| paths, methods (GET, POST, PUT, PATCH, DELETE), `summary`/`operationId`, `description`, first tag | method, route, name, description, group |
| path, query and header parameters (path-level ones too) | `Route`, `Query`, `Header` parameters; names like `X-Request-Id` become `xRequestId` bound from the header |
| `type`, `format` (date, date-time, uuid, email, uri, ipv4, ipv6, time, binary), `enum`, `default`, `example(s)`, `required` | type, format, allowed values, default, example, required |
| `minLength`, `maxLength`, `pattern`, `minimum`, `maximum`, exclusive bounds, `minItems`, `maxItems`, array `items` | constraints, item type |
| JSON request body properties (`allOf` merged, `$ref`s resolved) | `Body` parameters; nested objects keep their (sanitized) schema |
| `multipart/form-data` / urlencoded properties, `format: binary`, `encoding.contentType` | `Form` parameters, `File`s with allowed content types |
| first 2xx JSON response: schema and example | response schema (references inlined) and example |
| `security` | `requireAuthorization` (empty `security: []` → `allowAnonymous`) |
| `x-dynamic-endpoints-processor`, `x-dynamic-endpoints-processor-config` | processor and its configuration ([below](#a-processor-per-operation)) |

- **Reported, not guessed:** cookie parameters, `oneOf`/`anyOf` (with or without a `discriminator`), `multipleOf`, `uniqueItems`,
  unknown formats, patterns inside object schemas, callbacks, HEAD/OPTIONS/TRACE, non-JSON bodies and the like are listed per
  operation under `unmapped`.
- **Safe by default:** imported endpoints are **disabled** (`enabled=true` to change that) until somebody reviewed them.
  Operations that were imported before, or whose method and route already exist, are skipped – unless you
  [re-import](#re-importing-a-changed-document) with `mode=upsert` or `mode=sync`.
- Every skeleton is validated like any definition. When one is invalid nothing is written (`422`), unless `skipInvalid=true`.
- `IDynamicEndpointOpenApiImporter.Convert(document, options)` maps without validating, matching or saving anything.

## Options

| Query (admin API) | `OpenApiImportOptions` | |
|---|---|---|
| `processor` | `Processor`, `ProcessorConfig` | processor (and configuration) of the operations nothing else chose one for. Default: `options.DefaultProcessor` |
| `processorByTag=tag:processor` (repeatable) | `ProcessorsByTag` | processor (and configuration) per tag – [precedence](#a-processor-per-operation) |
| `mock` | `Mock` | every operation answers with its documented response – [mock mode](#mock-mode) |
| `mode` | `Mode` | `create` (default), `upsert` or `sync` – [re-importing](#re-importing-a-changed-document) |
| `documentId` | `DocumentId` | identity of the document that re-imports match. Default: `info.title` |
| `routePrefix` | `RoutePrefix` | prepended to every path, e.g. `/partners/v1` |
| `group` | `Group` | group of all endpoints. Default: the first tag of each operation |
| `tag` (repeatable) | `Tags` | only operations with one of these tags |
| `enabled` | `Enabled` | create the endpoints enabled. Default: disabled (disabled endpoints don't take part in route conflict checks either) |
| `skipInvalid` | `SkipInvalid` | write the valid operations even when others are invalid |
| `dryRun` | `DryRun` | report what would be created, updated or deleted and what couldn't be mapped, write nothing |

Configurations don't fit in a query string. For them, send the document wrapped together with the options – the query
still works and wins over the body:

```json
{
  "document": { "openapi": "3.0.3", "info": { "title": "Shop", "version": "1" }, "paths": { … } },
  "options": {
    "mode": "upsert",
    "processor": "http-forward",
    "processorConfig": { "url": "https://legacy.example.com/api" },
    "processorsByTag": {
      "Reports": { "processor": "sql-query", "processorConfig": { "connection": "reports", "query": "select * from reports" } }
    }
  }
}
```

The result lists every operation with its `action` (`Create`, `Update` with the changed properties in `changes`, `Unchanged`,
`Delete`, `Skip` with a `reason`, `Invalid` with `errors`), the endpoint `id`, the `processor` with its `processorSource` and
`processorReason`, the generated `definition` and the `unmapped` details – plus the document id, its `tags` and document-level
`warnings`.

## A processor per operation

Mark operations in the document itself, on the operation, on a path item (all its operations) or at the root (all operations):

```yaml
openapi: 3.0.3
info: { title: Shop, version: "1" }
x-dynamic-endpoints-processor: http-forward              # everything not chosen otherwise
x-dynamic-endpoints-processor-config: { url: "https://legacy.example.com/api" }
paths:
  /orders/{id}:
    get:
      x-dynamic-endpoints-processor: sql-query          # just this operation
      x-dynamic-endpoints-processor-config: { connection: shop, query: "select * from orders where id = @id", result: Row }
```

…or map tags to processors when importing (`ProcessorsByTag`, `processorByTag=Reports:sql-query`, `--processor-by-tag Reports=sql-query`).
Each operation gets the first processor of this list:

| # | Source (`processorSource`) | |
|---|---|---|
| 1 | `OperationExtension` | `x-dynamic-endpoints-processor` on the operation |
| 2 | `PathExtension` | `x-dynamic-endpoints-processor` on the path item |
| 3 | `Tag` | the mapping of the operation's first tag that has one (tags match case-insensitively) – not in mock mode |
| 4 | `DocumentExtension` | `x-dynamic-endpoints-processor` at the root of the document |
| 5 | `Mock` | `mock=true`: the built-in `response` processor with the documented response |
| 6 | `Option` | the `processor` option – not in mock mode |
| 7 | `Default` | none: `DynamicEndpointsOptions.DefaultProcessor` applies (the definition has no processor) |

The configuration always comes from the same place as the processor: a `-config` extension next to the chosen
`x-dynamic-endpoints-processor`, the tag mapping's `processorConfig`, or `ProcessorConfig`. A `-config` extension without a processor
next to it is ignored and reported. On a [re-import](#re-importing-a-changed-document), `Kept` says the endpoint keeps the processor
an admin chose.

## Mock mode

`mock=true` (`Mock = true`, `--mock`, the **Mock** checkbox in the wizard) turns a document into a working mock API. Every
operation gets the built-in [`response` processor](built-in-processors.md), configured from its first 2xx
response:

- **status code:** the response's (`201`, `204`; a `2XX` range answers `200`). Without a 2xx response, the first documented status
  (e.g. `410`); without any (only `default`), an empty `204`.
- **body:** the response's `example`, or the first of its `examples`, or – when it has neither – one generated from its schema,
  the same way the [request snippets](admin-api.md#snippets) generate examples (examples, defaults, enums, formats, limits;
  `allOf` merged, the first `oneOf`/`anyOf` alternative). JSON responses become a `body`, `text/*` responses a `text`, other
  media types an empty response with the status code.
- **content type:** JSON media types other than `application/json` (e.g. `application/problem+json`) and text types are kept.

```bash
curl -X POST "http://localhost:5000/api/admin/endpoints/import/openapi?mock=true&enabled=true" \
  -H "Content-Type: application/yaml" --data-binary @petstore.yaml
curl http://localhost:5000/pets/1      # → 200 with the example of GET /pets/{petId}
```

Extensions in the document (`x-dynamic-endpoints-processor` on the operation, path or root) still win: an operation that has to
reach a real backend keeps it. Tag mappings and the `processor` option are ignored. Mock mode needs the processor registered
(`AddResponseTemplateProcessor()`, or `AddBuiltInProcessors()`); without it the operations are invalid with a message saying so.
Placeholders like `{{petId}}` in examples are rendered by the template, so an example can echo request values.

## Re-importing a changed document

Every imported definition records where it came from in its `origin` – stored with the definition, no schema changes:

```json
"origin": { "kind": "openapi", "document": "Shop", "operation": "getOrder" }
```

`document` is the `documentId` option, or the document's `info.title`. `operation` is the `operationId`, or method and path
(`GET /orders/{id}`) for operations without one. Use a `documentId` of your own when the title changes, or to import one document
twice (e.g. under two route prefixes).

| `mode` | |
|---|---|
| `create` (default) | creates the operations that weren't imported yet; the others are skipped |
| `upsert` | also updates the endpoints imported from the same operation before |
| `sync` | like `upsert`, and deletes the endpoints imported from this document whose operation is gone from it |

- **Matching:** an operation is paired with the endpoint that has its origin; when its `operationId` changed, with the endpoint of
  the same document with its method and route.
- **Hand-made endpoints are never touched.** An endpoint without the document's origin is neither updated nor deleted: when an
  operation's route belongs to one, the operation is skipped with a `reason`. Endpoints imported before 0.4 have no origin and count
  as hand-made.
- **Sync deletes only what is gone.** Operations left out by `tag`, or that can't be mapped (HEAD), are not deleted.
- **All or nothing:** the whole set is validated first – route conflicts included – through the same machinery as the
  [definitions import](export-import-gitops.md): with `dryRun=true` you get the diff per operation, and nothing is written when
  anything is invalid. Writes create revisions, change events and audit entries like any change.

On an update, the document owns what it describes, and admins own the rest:

| The document owns (replaced) | Admins own (kept) |
|---|---|
| method, route, parameters, `requireAuthorization`/`allowAnonymous`, `origin` | enabled state, tenant, rules, request validators, request example |
| name, description, group, response schema and example – when the document has them (otherwise kept) | authorization and rate limiting policies, rate limit, caching |
| processor and its configuration – when an extension, a tag mapping, the `processor` option or mock mode chose one | processor and its configuration – when only the default applied (`processorSource: Kept`) |
| | validators attached to a parameter that still exists (same name and source) |

## Together with the rest

- Imported endpoints are created like any other: each gets its first revision ([history](drafts-and-history.md)), raises a
  `Created` [change event](change-events.md) and is [audited](audit-log.md); updates and deletes too.
- Through a [tenant's admin API](multi-tenancy.md#drafts-history-export-and-import-per-tenant) the skeletons belong to the
  tenant, "already exists" means "the tenant already has it", and a sync deletes only the tenant's own imported endpoints – never
  shared ones or another tenant's.
