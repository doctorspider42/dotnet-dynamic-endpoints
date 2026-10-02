# Built-in processors: HTTP forward, webhook, response template, SQL

Batteries included, but opt-in: nothing is registered until you ask for it. Their configuration is typed and validated on save
(unknown properties are errors).

```csharp
builder.Services.AddDynamicEndpoints()
    .AddBuiltInProcessors(o => o.AllowedHosts.Add("*.internal.example.com"))   // http-forward, webhook, response
    .AddSqlQueryProcessor(_ => new NpgsqlConnection(readOnlyConnectionString));  // DynamicEndpoints.Sql package

builder.Services.AddHttpClient("DynamicEndpoints").AddStandardResilienceHandler();  // optional: the processors' named client
```

One at a time: `AddHttpForwardProcessor()`, `AddWebhookProcessor()`, `AddResponseTemplateProcessor()`, each with an optional
name of its own.

| Processor | Configuration (excerpt) | |
|---|---|---|
| `http-forward` | `url` (`https://backend/orders/{id}`), `method`, `headers`, `forwardHeaders`, `body`, `bodyTemplate`, `timeoutSeconds`, `responseTemplate`, `statusCode` | proxies to another service through `IHttpClientFactory` and relays (or maps) its response |
| `webhook` | `url`, `method`, `headers`, `payload`, `retries`, `retryDelayMilliseconds`, `timeoutSeconds`, `signingSecretConfigurationKey`, `signatureHeader`, `background`, `statusCode` | JSON webhook with exponential back-off, HMAC-SHA256 signature and an `X-Webhook-Delivery` id |
| `response` | `body` (JSON template) or `text`, `statusCode`, `contentType`, `headers` | mock APIs, fixed answers, request-to-response mapping |
| `sql-query` | `query` with `@name` placeholders, `result` (`Rows`/`Row`/`Value`), `maxRows`, `timeoutSeconds`, `connection` | read-only SQL, see [below](#sql-dynamicendpointssql) |

```json
{ "processor": "http-forward", "processorConfig": {
    "url": "https://crm.internal.example.com/customers/{id}",
    "headers": { "X-Api-Key": "{config:Crm:ApiKey}" },
    "responseTemplate": { "id": "{{id}}", "name": "{{response.data.fullName}}" } } }
```

- **Templates:** URLs use `{name}` and every value is URL-encoded, so a parameter can't add path segments or change the host
  (placeholders in scheme, host and port are rejected). JSON and text templates use `{{name}}`, `{{address.city}}`,
  `{{response.items[0]}}`; a string that is only a placeholder keeps the JSON type. Values are inserted, never evaluated.
  `responseTemplate` sees the parameters, `{{response…}}` (the parsed upstream body) and `{{status}}`.
- **Request body** of `http-forward` (`body`): `Auto` (default: the `Body` parameters, if the endpoint has any), `Parameters`
  (all of them), `BodyParameters` or `None`; `bodyTemplate` builds it from a template instead.
- **Secrets** stay out of definitions: header values reference configuration with `{config:Section:Key}` (checked on save), the
  webhook secret is a configuration key. The signature is sent as `sha256=<hex>` in `X-Webhook-Signature`.
- **SSRF:** admins choose the targets. Restrict them with `AllowedHosts` when admins aren't fully trusted. It's checked on save and
  on every call.
- **Upstream failures:** timeouts are `504`, connection errors `502`, responses above `MaxResponseBodySize` (10 MB) `502`. Webhooks
  retry network errors, timeouts, `408`, `429` and `5xx` (honouring `Retry-After`), and answer `502` once they give up. With
  `background: true` they answer `202` right away and deliver from an in-memory queue (lost on shutdown).

## SQL: `DynamicEndpoints.Sql`

A read-only, parameterized SQL query processor: an admin writes a `SELECT`, the endpoint returns the rows as JSON. Works with any
ADO.NET provider (SQL Server, PostgreSQL, SQLite, MySQL, …). No third-party dependencies.

```csharp
builder.Services.AddDynamicEndpoints()
    .AddSqlQueryProcessor(_ => new NpgsqlConnection(builder.Configuration.GetConnectionString("ReadOnly")),
        o => o.Connections["reporting"] = _ => new NpgsqlConnection(reportingConnectionString));
```

```json
{
  "method": "GET",
  "route": "/customers",
  "processor": "sql-query",
  "processorConfig": {
    "query": "SELECT id, name FROM customers WHERE country = @country ORDER BY name",
    "result": "Rows",
    "maxRows": 100,
    "timeoutSeconds": 10
  },
  "parameters": [{ "name": "country", "source": "Query", "required": true, "maxLength": 2 }]
}
```

| Setting | |
|---|---|
| `query` | one `SELECT`, `WITH … SELECT` or `VALUES` statement with `@name` placeholders |
| `connection` | a named connection of `SqlQueryProcessorOptions.Connections` (default: the one passed to `AddSqlQueryProcessor`) |
| `result` | `Rows` (array, at most `maxRows`), `Row` (first row or 404), `Value` (first column of the first row or 404) |
| `maxRows`, `timeoutSeconds` | limits, 100 rows and 30 s by default |

`SqlQueryProcessorOptions` also has `ParameterPrefix` (`@` by default, `:` for Oracle) and `RollBackTransaction` (default on).

Treat "can edit definitions" as "can read what the connection's database user can read":

- **Parameters only.** Placeholders (`@country`) are bound to the validated request parameters of the same name as
  `DbParameter`s. Values are never concatenated into the SQL text. Missing parameters are `NULL`; arrays and objects are passed as
  JSON text.
- **Read-only checks on save.** A single statement starting with `SELECT`, `WITH` or `VALUES`; data-changing and administrative
  keywords (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `ALTER`, `CREATE`, `INTO`, `EXEC`, `CALL`, `COPY`, `PRAGMA`, `SET`, …) are
  rejected outside string literals and comments, and so are MySQL executable comments (`/*! … */`). The checks run again before
  every execution.
- **Rolled-back transaction.** Every query runs in a transaction that is always rolled back, so side effects that slip through
  are undone where the database supports it.
- **These checks are a safety net, not a sandbox.** Functions with side effects (e.g. `pg_terminate_backend`, `xp_cmdshell`,
  `lo_export`, `nextval`) can't be detected lexically. **Use a dedicated database user with `SELECT` rights on exactly the
  tables and views the endpoints may expose**, and keep the admin API behind authorization.
- Responses can contain anything the query selects: don't select secrets, and use `maxRows` to keep responses small.

## With multi-tenancy

The processors see the request's tenant like any processor (`request.Tenant`), but their configuration is per definition: a
tenant's endpoint can forward to the tenant's own backend, a shared endpoint forwards every tenant to the same target.
`AllowedHosts` and the SQL connections are set by the application and apply to every tenant. Named connections aren't tied to
a tenant: any definition may pick any `connection`. When tenants edit their own definitions through a
[tenant admin API](multi-tenancy.md#admin-api), only register `sql-query` if every connection may be read by every tenant.
