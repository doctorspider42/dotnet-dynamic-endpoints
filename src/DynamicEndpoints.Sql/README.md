# DynamicEndpoints.Sql

A read-only, parameterized SQL query processor for [DynamicEndpoints](https://github.com/doctorspider42/dotnet-dynamic-endpoints):
an admin writes a `SELECT`, the endpoint returns the rows as JSON. Works with any ADO.NET provider (SQL Server, PostgreSQL,
SQLite, MySQL, …). No third-party dependencies.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/LICENSE)

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

## Security

Treat "can edit definitions" as "can read what the connection's database user can read".

- **Parameters only.** Placeholders (`@country`) are bound to the validated request parameters of the same name as
  `DbParameter`s. Values are never concatenated into the SQL text. Missing parameters are `NULL`; arrays and objects are passed as
  JSON text.
- **Read-only checks on save.** A single statement starting with `SELECT`, `WITH` or `VALUES`; data-changing and administrative
  keywords (`INSERT`, `UPDATE`, `DELETE`, `MERGE`, `DROP`, `ALTER`, `CREATE`, `INTO`, `EXEC`, `CALL`, `COPY`, `PRAGMA`, `SET`, …) are
  rejected outside string literals and comments, and so are MySQL executable comments (`/*! … */`). The lexer reads every
  dialect-specific construct (backslash escapes, dollar quoting, brackets, backticks) as code, so it can't be tricked into skipping
  something the database runs. The checks run again before every execution.
- **Rolled-back transaction.** Every query runs in a transaction that is always rolled back (`RollBackTransaction`, default on), so
  side effects that slip through are undone where the database supports it.
- **These checks are a safety net, not a sandbox.** Functions with side effects (e.g. `pg_terminate_backend`, `xp_cmdshell`,
  `lo_export`, `nextval`) can't be detected lexically. **Use a dedicated database user with `SELECT` rights on exactly the
  tables and views the endpoints may expose**, and keep the admin API behind authorization.
- Responses can contain anything the query selects: don't select secrets, and use `maxRows` to keep responses small.

## Multi-tenancy

With `UseMultiTenancy()`, connections are assigned to tenants:

```csharp
builder.Services.AddDynamicEndpoints()
    .UseMultiTenancy(t => t.FromHeader("X-Tenant-Id"))
    .AddSqlQueryProcessor(_ => new NpgsqlConnection(appReadOnly), o =>
    {
        o.Connections["acme-reports"] = _ => new NpgsqlConnection(acmeReadOnly);
        o.AllowTenants("acme-reports", "acme");            // "" is the default connection
    });
```

- **Shared endpoints** (no tenant) may use every connection.
- **A tenant's endpoints** may only use the connections assigned to that tenant (`AllowTenants`, or
  `SqlQueryProcessorOptions.ConnectionTenants`). Unassigned connections – the default one included – are off limits, so a tenant's
  admin can't query the application's or another tenant's database.
- Checked when the definition is saved (the error lists only the connections available to that tenant) and again before every
  run: an endpoint whose connection was taken away from its tenant answers `500` instead of running.

📖 [Documentation](https://doctorspider42.github.io/dotnet-dynamic-endpoints/articles/built-in-processors.html) ·
📝 [Changelog](https://github.com/doctorspider42/dotnet-dynamic-endpoints/blob/main/CHANGELOG.md)
