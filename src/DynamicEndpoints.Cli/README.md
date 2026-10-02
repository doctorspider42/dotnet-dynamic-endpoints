# DynamicEndpoints.Cli

`dynamic-endpoints`, a command-line tool for [DynamicEndpoints](https://github.com/doctorspider42/dotnet-dynamic-endpoints):
keep endpoint definitions in Git and list, export, diff and push them through the admin REST API (`MapDynamicEndpointsAdmin`).
Made for CI: meaningful exit codes, credentials from environment variables, JSON output on request.

```bash
dotnet tool install --global DynamicEndpoints.Cli

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

YAML files are converted locally, so the server doesn't need `DynamicEndpoints.Yaml`. A typical pipeline:

```yaml
- run: dotnet tool install --global DynamicEndpoints.Cli
- run: dynamic-endpoints diff endpoints.yaml || [ $? -eq 2 ]     # show the plan in pull requests
- run: dynamic-endpoints push endpoints.yaml --sync              # on main
  env:
    DYNAMIC_ENDPOINTS_URL: ${{ vars.ADMIN_URL }}
    DYNAMIC_ENDPOINTS_TOKEN: ${{ secrets.ADMIN_TOKEN }}
```
