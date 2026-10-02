# Export, import & GitOps

Keep the definitions in Git, review changes in pull requests and push them from CI:

```bash
dotnet tool install --global DynamicEndpoints.Cli

dynamic-endpoints export -o endpoints.yaml --url https://api.example.com/api/admin/endpoints --api-key "$ADMIN_KEY"
dynamic-endpoints diff endpoints.yaml        # exit code 2 when the server differs from the file
dynamic-endpoints push endpoints.yaml --sync # create, update and delete until the server matches the file
```

- **Stable format** (`dynamic-endpoints/v1`): sorted by route, method and id, without revisions and timestamps, indented with `\n`
  line endings, so diffs in pull requests show real changes only. JSON is built in; YAML needs `DynamicEndpoints.Yaml` and
  `.AddYamlFormat()`.
- **Modes:** `create` (only new endpoints), `upsert` (default: create and replace) and `sync` (also delete what isn't in the file).
- **Matching:** by `id`; definitions without one are matched by method and route, so hand-written files work too.
- **All or nothing:** the whole import is validated first – including route conflicts among the imported endpoints – and nothing
  is written when any endpoint is invalid (`422`). The dry run (`?dryRun=true`, `diff`) reports per endpoint `Create`, `Update`
  (with the changed properties), `Delete`, `Unchanged`, `Skip` or `Invalid`.
- **In code:** `IDynamicEndpointTransfer.ExportAsync(…)` / `ImportAsync(export, new() { Mode = DynamicEndpointImportMode.Sync, DryRun = true })`.
  Writes go through the registered store one by one (not in one transaction); the routing table is updated once at the end.
  `DynamicEndpointExport.ToJson()` / `FromJson()` write and read the format; a plain array of definitions is read too.

## Admin API

| Method | Path | |
|---|---|---|
| `GET` | `/export?id=…&format=json\|yaml` | all definitions, or those with the given ids (repeat `id`). Without `format` the `Accept` header decides, JSON by default |
| `POST` | `/import?mode=create\|upsert\|sync&dryRun=true` | import an export (`application/json`, or `application/yaml` with `DynamicEndpoints.Yaml`); `422` when anything is invalid |

```bash
curl https://api.example.com/api/admin/endpoints/export?format=yaml > endpoints.yaml
curl -X POST "https://api.example.com/api/admin/endpoints/import?mode=sync&dryRun=true" \
  -H "Content-Type: application/yaml" --data-binary @endpoints.yaml
```

```yaml
format: dynamic-endpoints/v1
endpoints:
  - id: 0199a4f2-3c1e-7b4a-9d2e-5f6a7b8c9d0e
    method: GET
    route: /orders/{id}
    processor: orders
    parameters:
      - name: id
        source: Route
        type: Integer
        required: true
```

## YAML: `DynamicEndpoints.Yaml`

```csharp
builder.Services.AddDynamicEndpoints().AddYamlFormat();
```

Export, import and the [OpenAPI import](openapi-import.md) then accept and return YAML. Built on
[YamlDotNet](https://github.com/aaubry/YamlDotNet). In code, `DynamicEndpointsYaml.Parse(yaml)` and `DynamicEndpointsYaml.Write(node)`
convert between YAML and `JsonNode`. Plain scalars follow the YAML 1.2 core schema, and strings that would read as something else
(`"true"`, `"007"`) are written quoted, so a round trip never changes a type.

## The `dynamic-endpoints` CLI

A .NET tool (`DynamicEndpoints.Cli`) that talks to the admin REST API. Made for CI: meaningful exit codes, credentials from
environment variables, JSON output on request. YAML files are converted locally, so the server doesn't need `DynamicEndpoints.Yaml`.

```bash
export DYNAMIC_ENDPOINTS_URL=https://api.example.com/api/admin/endpoints
export DYNAMIC_ENDPOINTS_API_KEY=…            # sent as X-Api-Key (or --api-key-header), or DYNAMIC_ENDPOINTS_TOKEN for a bearer token

dynamic-endpoints list
dynamic-endpoints export -o endpoints.yaml    # .json or .yaml; or ids: export 0199a4f2-… -o one.json
dynamic-endpoints diff endpoints.yaml         # what 'push --sync' would change
dynamic-endpoints push endpoints.yaml --sync  # create, update, delete
dynamic-endpoints import-openapi partner-api.yaml --processor http-forward --route-prefix /partners --dry-run
```

```text
$ dynamic-endpoints diff endpoints.yaml
+ create    GET    /orders/{id}  (Get order)
~ update    POST   /orders  (Create order)  [parameters, processorConfig]
- delete    GET    /legacy
Dry run: 1 to create, 1 to update, 1 to delete, 4 unchanged, 0 skipped, 0 invalid.
```

| Command | |
|---|---|
| `list` | endpoints with their status on the instance that answered |
| `export [<id>…]` | the stable export format; `--output <file>`, `--format json\|yaml` (default: by extension) |
| `push <file>` (`import`) | `--mode create\|upsert\|sync` (default `upsert`), `--sync`, `--dry-run` |
| `diff <file>` | `push --sync --dry-run`, with exit code 2 when there are differences |
| `import-openapi <file>` | skeletons from OpenAPI 3.x; `--processor`, `--route-prefix`, `--group`, `--tag`, `--enabled`, `--skip-invalid`, `--dry-run` |

Options: `--url` (`DYNAMIC_ENDPOINTS_URL`), `--api-key` (`DYNAMIC_ENDPOINTS_API_KEY`), `--api-key-header`
(`DYNAMIC_ENDPOINTS_API_KEY_HEADER`, default `X-Api-Key`), `--token` (`DYNAMIC_ENDPOINTS_TOKEN`), `-H "Name: value"` (repeatable),
`--timeout <seconds>`, `--json` (print the server's response), `--verbose` (unchanged endpoints, unmapped OpenAPI details).

| Exit code | |
|---|---|
| `0` | success (for `diff`: no differences) |
| `1` | rejected – invalid definitions, nothing was written |
| `2` | `diff` found differences |
| `3` | usage error (unknown command or option, missing file or URL) |
| `4` | the admin API could not be reached, or answered with an unexpected status (e.g. 401) |

A typical pipeline:

```yaml
- run: dotnet tool install --global DynamicEndpoints.Cli
- run: dynamic-endpoints diff endpoints.yaml || [ $? -eq 2 ]     # show the plan in pull requests
- run: dynamic-endpoints push endpoints.yaml --sync              # on main
  env:
    DYNAMIC_ENDPOINTS_URL: ${{ vars.ADMIN_URL }}
    DYNAMIC_ENDPOINTS_TOKEN: ${{ secrets.ADMIN_TOKEN }}
```

## Together with the rest

- **Drafts:** exports contain published endpoints only. Endpoints that exist only as a draft aren't exported, matched or deleted by
  a sync, and drafts of published endpoints stay drafts.
- **History:** an import is a series of ordinary changes, so every created or updated endpoint gets a revision, and a bad push
  can be [rolled back](drafts-and-history.md) endpoint by endpoint. Imports raise [change events](change-events.md), land in the
  [audit log](audit-log.md) and reach the other instances.
- **Multi-tenancy:** point the CLI at a tenant's admin API (`--url https://api.example.com/api/tenants/acme/endpoints`) and it
  exports, diffs and syncs only that tenant's endpoints – `push --sync` never deletes anybody else's. Through the global admin API
  definitions carry their `tenant`. See [Multi-tenancy](multi-tenancy.md#drafts-history-export-and-import-per-tenant).
