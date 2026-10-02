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
| `GET` | `/processors` · `/validators` | building blocks for the UI |
| `GET` · `POST` | `/drafts` | all drafts · draft a new endpoint |
| `GET` · `PUT` · `DELETE` | `/{id}/draft` | the draft of an endpoint (`{ definition, publishAt, comment }`) |
| `GET` | `/{id}/draft/diff` | what publishing would change |
| `POST` | `/{id}/publish` | publish the draft |
| `GET` | `/{id}/revisions` · `/{id}/revisions/{revision}` | history |
| `GET` | `/{id}/diff?from=3&to=5` | differences between revisions (`to` defaults to the published one) |
| `POST` | `/{id}/revisions/{revision}/rollback` | roll back |
| `GET` | `/{id}/snippets?baseUrl=` | example request + curl, HTTPie and C# snippets |
| `POST` | `/snippets?baseUrl=` | the same for an unsaved definition (editor preview) |
| `GET` | `/export?id=…&format=json\|yaml` | definitions in the stable export format |
| `POST` | `/import?mode=create\|upsert\|sync&dryRun=true` | import an export; the dry run is the diff |
| `POST` | `/import/openapi?processor=&routePrefix=&group=&tag=&enabled=&skipInvalid=&dryRun=` | skeletons from an OpenAPI 3.x document |
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
- **OpenAPI import:** [Import from OpenAPI](openapi-import.md).
- **A panel on top:** [`MapDynamicEndpointsAdminUI()`](admin-ui.md).

## Snippets

The example request is built from parameter examples, then defaults, allowed values and finally values that satisfy the
constraints (`Range(5, 100)` → `5`, `Email()` → `user@example.com`, `MinLength(8)` → `stringxx`, custom object schemas property by
property). Optional parameters with a default and no example are left out. Documented required headers
(`o.OpenApi.AddHeader(…, required: true)`) and credentials of the security schemes show up as placeholders (`<api-key>`,
`Bearer <token>`). `baseUrl` defaults to the address the admin API was called on; an explicit one must be an absolute http(s) URL.

In code: `IDynamicEndpointSnippetGenerator.Generate(definition, baseUrl)` (or `CreateExample(…)` for the request alone).

Snippets know nothing about tenants: they use the route of the definition, without a tenant route prefix, and contain a
tenant header only when it's documented as a required header (`o.OpenApi.AddHeader("X-Tenant-Id", "Tenant.", required: true)`).
