# Import from OpenAPI

Got a contract first? Turn an OpenAPI 3.x document into endpoint skeletons:

```bash
curl -X POST "https://api.example.com/api/admin/endpoints/import/openapi?processor=http-forward&routePrefix=/partners&dryRun=true" \
  -H "Content-Type: application/json" --data-binary @partner-api.json      # YAML with DynamicEndpoints.Yaml
```

```csharp
var result = await importer.ImportAsync(document, new OpenApiImportOptions { Processor = "orders", DryRun = true });   // IDynamicEndpointOpenApiImporter
```

…or with the CLI: `dynamic-endpoints import-openapi partner-api.yaml --processor http-forward --route-prefix /partners --dry-run`
([GitOps](export-import-gitops.md#the-dynamic-endpoints-cli)).

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

- **Reported, not guessed:** cookie parameters, `oneOf`/`anyOf`, `multipleOf`, `uniqueItems`, unknown formats, patterns inside
  object schemas, callbacks, HEAD/OPTIONS/TRACE, non-JSON bodies and the like are listed per operation under `unmapped`.
- **Safe by default:** imported endpoints are **disabled** (`enabled=true` to change that) until somebody reviewed them, and get
  the given `processor` (or `DefaultProcessor`). Operations whose method and route already exist are skipped, so re-importing is safe.
- Every skeleton is validated like any definition. When one is invalid nothing is created (`422`), unless `skipInvalid=true`.
- `IDynamicEndpointOpenApiImporter.Convert(document, options)` maps without validating or saving anything.

## Options

| Query (admin API) | `OpenApiImportOptions` | |
|---|---|---|
| `processor` | `Processor`, `ProcessorConfig` | processor (and its configuration) of every imported endpoint. Default: `options.DefaultProcessor` |
| `routePrefix` | `RoutePrefix` | prepended to every path, e.g. `/partners/v1` |
| `group` | `Group` | group of all endpoints. Default: the first tag of each operation |
| `tag` (repeatable) | `Tags` | only operations with one of these tags |
| `enabled` | `Enabled` | create the endpoints enabled. Default: disabled (disabled endpoints don't take part in route conflict checks either) |
| `skipInvalid` | `SkipInvalid` | create the valid endpoints even when others are invalid |
| `dryRun` | `DryRun` | report what would be created and what couldn't be mapped, write nothing |

The result lists every operation with its `action` (`Create`, `Skip`, `Invalid`), the generated `definition`, `unmapped` details
and `errors`, plus document-level `warnings`.

## Together with the rest

- Imported endpoints are created like any other: each gets its first revision ([history](drafts-and-history.md)), raises a
  `Created` [change event](change-events.md) and is [audited](audit-log.md).
- Through a [tenant's admin API](multi-tenancy.md#drafts-history-export-and-import-per-tenant) the skeletons belong to the
  tenant, and "already exists" means "the tenant already has it".
