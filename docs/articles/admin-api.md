# Admin REST API: `MapDynamicEndpointsAdmin()`

| Method | Path | |
|---|---|---|
| `GET` | `/` | all definitions with runtime status (`Active`, `Disabled`, `Invalid`, `Pending`) |
| `GET` | `/{id}` | single definition |
| `POST` | `/` | create & publish |
| `PUT` | `/{id}` | replace (requires matching `revision`) |
| `DELETE` | `/{id}` | delete |
| `POST` | `/{id}/enable` · `/{id}/disable` | toggle |
| `POST` | `/validate` | dry run |
| `POST` | `/reload` | re-read the store |
| `GET` | `/processors` · `/validators` | building blocks for the UI |
| `GET` | `/tenants` · `/?tenant=acme` | tenants that own endpoints, the endpoints of one tenant ([multi-tenancy](multi-tenancy.md)) |
| `GET` | `/audit` · `/{id}/audit` | the [audit log](audit-log.md), when a queryable sink is configured |

`MapDynamicEndpointsTenantAdmin("/api/tenants/{tenant}/endpoints")` maps the same API for a single tenant.

It returns a `RouteGroupBuilder`, so secure it like any group: `.RequireAuthorization("admin")`. The prefix is reserved automatically.
