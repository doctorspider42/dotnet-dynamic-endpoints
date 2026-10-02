# Admin REST API: `MapDynamicEndpointsAdmin()`

| Method | Path | |
|---|---|---|
| `GET` | `/` | all definitions with runtime status (`Active`, `Disabled`, `Invalid`, `Pending`, `Draft`) |
| `GET` | `/{id}` | single definition |
| `POST` | `/` | create & publish |
| `PUT` | `/{id}` | replace (requires matching `revision`) |
| `DELETE` | `/{id}` | delete |
| `POST` | `/{id}/enable` · `/{id}/disable` | toggle |
| `POST` | `/validate` | dry run |
| `POST` | `/reload` | re-read the store |
| `GET` | `/info` | what this admin API supports: tenancy, drafts & history, audit log, export formats ([below](#info)) |
| `GET` | `/processors` · `/validators` | building blocks for the UI |
| `GET` · `POST` | `/drafts` | all drafts · draft a new endpoint |
| `GET` · `PUT` · `DELETE` | `/{id}/draft` | the draft of an endpoint (`{ definition, publishAt, comment }`) |
| `GET` | `/{id}/draft/diff` | what publishing would change |
| `POST` | `/{id}/publish` | publish the draft |
| `GET` | `/{id}/revisions` · `/{id}/revisions/{revision}` | history |
| `GET` | `/{id}/diff?from=3&to=5` | differences between revisions (`to` defaults to the published one) |
| `POST` | `/{id}/revisions/{revision}/rollback` | roll back |
| `GET` | `/{id}/snippets?baseUrl=&tenant=` | example request + curl, HTTPie and C# snippets |
| `POST` | `/snippets?baseUrl=&tenant=` | the same for an unsaved definition (editor preview) |
| `GET` | `/export?id=…&format=json\|yaml` | definitions in the stable export format |
| `POST` | `/import?mode=create\|upsert\|sync&dryRun=true` | import an export; the dry run is the diff |
| `POST` | `/import/openapi?mode=create\|upsert\|sync&processor=&processorByTag=tag:processor&mock=&documentId=&routePrefix=&group=&tag=&enabled=&skipInvalid=&dryRun=` | endpoints from an OpenAPI 3.x document (or `{ document, options }`): a processor per operation, mocks, re-imports with a diff |
| `GET` | `/tenants` · `/?tenant=acme` | tenants that own endpoints, the endpoints of one tenant ([multi-tenancy](multi-tenancy.md)) |
| `GET` | `/audit` · `/{id}/audit` | the [audit log](audit-log.md), when a queryable sink is configured |

`MapDynamicEndpointsTenantAdmin("/api/tenants/{tenant}/endpoints")` maps the same API for a single tenant, drafts, history,
export and imports included ([multi-tenancy](multi-tenancy.md#drafts-history-export-and-import-per-tenant)).

It returns a `RouteGroupBuilder`, so secure it like any group: `.RequireAuthorization("admin")`. The prefix is reserved automatically.
Without an argument the prefix is `/_dynamic-endpoints`.

More on the groups of routes:

- **Drafts, revisions, diffs, rollback:** [Drafts, history & rollback](drafts-and-history.md). Stores without history answer `501`,
  a draft based on an outdated revision `409`.
- **Export & import:** [Export, import & GitOps](export-import-gitops.md), with the `dynamic-endpoints` CLI.
- **OpenAPI import:** [Import from OpenAPI](openapi-import.md) – processors per operation, mock mode, re-import and sync.
- **A panel on top:** [`MapDynamicEndpointsAdminUI()`](admin-ui.md).

## Snippets

The example request is built from parameter examples, then defaults, allowed values and finally values that satisfy the
constraints (`Range(5, 100)` → `5`, `Email()` → `user@example.com`, `MinLength(8)` → `stringxx`, custom object schemas property by
property). Optional parameters with a default and no example are left out. Documented required headers
(`o.OpenApi.AddHeader(…, required: true)`) and credentials of the security schemes show up as placeholders (`<api-key>`,
`Bearer <token>`). `baseUrl` defaults to the address the admin API was called on; an explicit one must be an absolute http(s) URL.

In code: `IDynamicEndpointSnippetGenerator.Generate(definition, baseUrl)` (or `CreateExample(…)` for the request alone).

**With multi-tenancy** a snippet is the request of a tenant: the tenant is filled into the tenant route prefix
(`https://api.example.com/t/acme/orders`) and sent in the header of the first `FromHeader()` resolver (`X-Tenant: acme`).

- An endpoint of a tenant always uses its own tenant; `?tenant=` is ignored for it.
- A shared endpoint uses `?tenant=`. Without it the prefix keeps its placeholder (`/t/{tenant}/status`), like the OpenAPI
  document of the shared endpoints, and no tenant header is sent.
- A [tenant's admin API](multi-tenancy.md#admin-api) always shows the requests of its own tenant.
- Tenants found by host or claim aren't part of the request, so nothing is added for them.

`Generate(definition, baseUrl, tenant)` and `CreateExample(definition, baseUrl, tenant)` do the same in code. They are default
interface methods, so your own `IDynamicEndpointSnippetGenerator` keeps compiling (and ignores the tenant until it overrides them).

## Info

`GET /info` answers with a `DynamicEndpointsAdminInfo`, so a client can offer only what works. The [admin panel](admin-ui.md)
reads it on start.

```json
{
  "tenancy": { "routePrefix": null, "routeParameter": null, "header": "X-Tenant" },
  "tenant": null,
  "revisions": true,
  "auditLog": true,
  "formats": ["json", "yaml"]
}
```

| Property | |
|---|---|
| `tenancy` | `null` without `UseMultiTenancy()`; otherwise the tenant route prefix and its parameter (if any) and the header of the first `FromHeader()` resolver (if any) |
| `tenant` | the tenant of a tenant admin API (`MapDynamicEndpointsTenantAdmin`), `null` for the full admin API |
| `revisions` | the store keeps drafts and history (`IDynamicEndpointRevisionStore`) |
| `auditLog` | a queryable audit log is configured, so `/audit` and `/{id}/audit` answer |
| `formats` | text formats of `/export`, `/import` and `/import/openapi`: `json`, plus `yaml` with `AddYamlFormat()` |
