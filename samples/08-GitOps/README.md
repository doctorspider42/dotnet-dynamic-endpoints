# 08 – GitOps: export, import, YAML and the CLI

Keep the definitions in Git, review changes in pull requests, push them from CI. The admin API exports a stable format (sorted,
no revisions or timestamps, `\n` line endings – diffs show real changes only) as JSON or YAML, and imports it with
`create`, `upsert` or `sync` – always validated as a whole first, with a dry run that is the plan. The `dynamic-endpoints` CLI of
this repository wraps it for pipelines: [`gitops.ps1`](gitops.ps1) / [`gitops.sh`](gitops.sh) run a full round trip against
the running sample with [`endpoints.yaml`](endpoints.yaml) as the desired state.

| File | What to look at |
|---|---|
| [`endpoints.yaml`](endpoints.yaml) | the desired state, hand-written: no ids, so endpoints are matched by method and route |
| [`GitOpsSeeder.cs`](GitOpsSeeder.cs) | the server's state before the first push – different on purpose: `GET /orders/{id}` changed, `GET /legacy` extra, two endpoints missing |
| [`Program.cs`](Program.cs) | `AddYamlFormat()`, and the admin API mapped twice: `/api/admin/endpoints` for the panel, `/api/ci/endpoints` behind an API key for the CLI |
| [`gitops.ps1`](gitops.ps1), [`gitops.sh`](gitops.sh) | list → diff (exit code 2: there are differences) → `push --sync` → diff (exit code 0) → export |
| [`gitops.http`](gitops.http) | the same through the admin API |

## Run it

```bash
dotnet run --project samples/08-GitOps          # terminal 1 – http://localhost:5108/admin/
./samples/08-GitOps/gitops.ps1                  # terminal 2 (PowerShell 7)
./samples/08-GitOps/gitops.sh                   # …or bash
```

Definitions in `gitops.db` (in the sample's folder, the working directory of `dotnet run`) – delete it to get the seeded state
back and run the scripts again. The scripts build and run the CLI from `src/DynamicEndpoints.Cli`; in a pipeline you would
`dotnet tool install --global DynamicEndpoints.Cli` and call `dynamic-endpoints`. They pass the URL and the key through
`DYNAMIC_ENDPOINTS_URL` and `DYNAMIC_ENDPOINTS_API_KEY`, like a pipeline would.

```text
== 2. The plan: what 'push --sync' would change (exit code 2 = there are differences)
+ create    GET    /health  (Health)
+ create    POST   /orders  (Create order)
~ update    GET    /orders/{id}  (Get order)  [processorConfig, parameters]
- delete    GET    /legacy  (Legacy endpoint)
Dry run: 2 to create, 1 to update, 1 to delete, 0 unchanged, 0 skipped, 0 invalid.
exit code 2
```

## Click around

- **Export** (toolbar above the list): all endpoints or the selected ones, JSON or YAML, with a preview.
- **Import**: paste `endpoints.yaml`, mode `sync` – the dry run always comes first and lists what would be created, updated
  (with the changed properties), deleted or is invalid. Break the file (e.g. `minimum: abc`) and nothing is written: `422`.
- After a push, open *Get order* → *History*: the import is an ordinary change with a revision, so you can diff it and roll it back.

## By hand

```bash
dotnet run --project src/DynamicEndpoints.Cli -- diff samples/08-GitOps/endpoints.yaml --url http://localhost:5108/api/ci/endpoints --api-key ci-secret-key
dotnet run --project src/DynamicEndpoints.Cli -- push samples/08-GitOps/endpoints.yaml --sync --url http://localhost:5108/api/ci/endpoints --api-key ci-secret-key
dotnet run --project src/DynamicEndpoints.Cli -- export -o my-endpoints.json --url http://localhost:5108/api/ci/endpoints --api-key ci-secret-key

curl -H "X-Api-Key: ci-secret-key" "http://localhost:5108/api/ci/endpoints/export?format=yaml"
curl -X POST -H "X-Api-Key: ci-secret-key" -H "Content-Type: application/yaml" --data-binary @samples/08-GitOps/endpoints.yaml \
  "http://localhost:5108/api/ci/endpoints/import?mode=sync&dryRun=true"
curl -i http://localhost:5108/api/ci/endpoints                  # 401 – no key
```

A pipeline, roughly:

```yaml
- run: dotnet tool install --global DynamicEndpoints.Cli
- run: dynamic-endpoints diff endpoints.yaml || [ $? -eq 2 ]     # show the plan in pull requests
- run: dynamic-endpoints push endpoints.yaml --sync              # on main
  env:
    DYNAMIC_ENDPOINTS_URL: ${{ vars.ADMIN_URL }}
    DYNAMIC_ENDPOINTS_API_KEY: ${{ secrets.ADMIN_API_KEY }}
```

## Read more

- [Export, import & GitOps](../../docs/articles/export-import-gitops.md) – the format, modes, matching, the CLI's commands,
  options and exit codes
- [Admin REST API](../../docs/articles/admin-api.md) · [Drafts, history & rollback](../../docs/articles/drafts-and-history.md) ·
  [Security](../../docs/articles/security.md)
- Next: [09 – Drafts, history & audit](../09-DraftsHistoryAudit/README.md), or the [overview of all samples](../../docs/articles/samples.md).
