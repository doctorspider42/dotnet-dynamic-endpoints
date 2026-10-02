# Security

- 🚫 **Nothing is executed from definitions.** JsonLogic is a closed set of operators. There's no scripting and no Roslyn.
- 🧨 **ReDoS-proof:** regex constraints run on `RegexOptions.NonBacktracking`. Backreferences and lookarounds are rejected on save.
- 📏 **Limits:** JSON body size (1 MB), form body size (30 MB) and JSON depth (32) by default. Bodies that aren't valid UTF-8 are rejected.
- 🧱 **Reserved prefixes:** the admin API is protected automatically, the rest via options. Clashes with the app's own endpoints are rejected.
- 🔑 **Policies:** authorization policies referenced by definitions must exist when the definition is saved.
- 🌐 **Outbound calls:** the built-in `http-forward` and `webhook` processors call whatever admins configure. Restrict the targets
  with `AllowedHosts` (checked on save and on every call) when admins aren't fully trusted. Placeholders can't change scheme, host
  or port, and every value is URL-encoded. Secrets stay in the configuration (`{config:Section:Key}`, `signingSecretConfigurationKey`).
  See [Built-in processors](built-in-processors.md).
- 🗄️ **SQL:** the `sql-query` processor binds parameters only and rejects anything but a single read-only statement, but it is a
  safety net, not a sandbox. Give it a database user that can read exactly what the endpoints may expose. With multi-tenancy,
  a tenant's endpoints can only use the connections assigned to that tenant (`AllowTenants`), never the default one unless it's
  assigned ([built-in processors](built-in-processors.md#with-multi-tenancy)).
- 🧾 **EF Core CRUD:** the `ef-crud` processor reaches only the entities and fields the application allowlisted with
  `AddEntityFrameworkCrud` – admins pick an entity by name, never a type. Request bodies are never bound to entities: only declared
  body parameters that are writable fields are written (keys, generated values, concurrency tokens and the tenant column never),
  and responses project to the exposed fields. Filters and sorting accept allowlisted fields only and run as expression trees with
  parameters – no dynamic LINQ, no SQL. Rows of entities with a tenant column are always filtered by the tenant, and entities without
  one are off limits for tenants unless allowed. `AllFields()` exposes columns added later too – prefer `Fields(…)` for tables that
  may grow secrets ([ef-crud](ef-crud.md)).
- 📥 **Imports** (export/import, OpenAPI) are validated as a whole like any definition, and nothing is written when one is
  invalid. Imported OpenAPI skeletons and scaffolded CRUD endpoints are disabled until somebody reviewed them.
- 🖥️ **Admin panel:** holds no data, sets `X-Content-Type-Options: nosniff`, `frame-ancestors 'none'` and
  `Referrer-Policy: no-referrer`. Secure the admin API it talks to, not only the panel.
- 🏢 **Tenants:** a tenant's admin API only sees and changes the tenant's endpoints, drafts and history; secure it with a policy
  that checks the user belongs to the tenant ([multi-tenancy](multi-tenancy.md#admin-api)).
- ⚠️ **The admin API is open by default.** Put `.RequireAuthorization(...)` on it. The same goes for `MapDynamicEndpointsAdminUI()`
  and `MapDynamicEndpointsTenantAdmin()`.
