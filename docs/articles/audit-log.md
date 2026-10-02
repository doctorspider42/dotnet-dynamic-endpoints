# Audit log

`AddAuditLog()` records every change made through this instance's manager: who made it, what changed and when.

```csharp
builder.Services.AddDynamicEndpoints()
    .AddAuditLog();                                          // ILogger, category "DynamicEndpoints.Audit"
 // .AddAuditLog(a => a.ToMemory())                          // latest 1000 entries, queryable – tests, development
 // .AddAuditLog(a => a.ToEntityFramework<AppDbContext>())   // a table in your own context, queryable
 // .AddAuditLog(a => a.ToLogger().To<ServiceBusAuditSink>())
```

```
Dynamic endpoint Updated GET /orders/{id} (0199…, tenant acme, revision 4) by alice: name: "Order" → "Order lookup"; parameters[0].maxLength: 10 → 20
```

It is built on [change events](change-events.md) with origin `Local`, so each change is audited once, on the instance that made
it. Changes picked up from other instances are not audited again.

Publishing a [draft](drafts-and-history.md), a rollback and every endpoint of an [import](export-import-gitops.md) are audited like
any other change; saving or discarding a draft is not, since nothing is routed. The audit log and the revision history
complement each other: the history keeps every revision for diffs and rollbacks, the audit log adds who made the change, the
request's trace id and the instance. Audit diffs address list items by index (`parameters[0].maxLength`), revision diffs by name
(`parameters[quantity].maximum`).

## Entries

| Field | |
|---|---|
| `Kind`, `EndpointId`, `Timestamp` | what happened to which endpoint, when |
| `User` | the user of the HTTP request (`Identity.Name`, else the `NameIdentifier` / `sub` claim); `null` outside requests (seeders, jobs) |
| `Changes` | the diff, property by property: `Path` (`parameters[0].maxLength`), `Before`, `After`. Revision and timestamps are left out |
| `Previous`, `Definition` | the complete definitions before and after (switch off with `IncludeDefinitions = false`) |
| `Tenant`, `Method`, `Route`, `Name`, `Revision` | the endpoint, after the change (before it, for deletions) |
| `TraceId`, `InstanceId` | the request and the instance that made the change |

```csharp
.AddAuditLog(a => a.ToMemory().Configure(o =>
{
    o.ResolveUser = http => http.User.FindFirst("email")?.Value;
    o.IncludeDefinitions = false;
}))
```

## Sinks

A sink is an `IDynamicEndpointAuditSink` (`WriteAsync(entry, ct)`). Every registered sink gets every entry; a failing sink is
logged and doesn't stop the others or undo the change. Sinks that can also be queried implement `IDynamicEndpointAuditLog`.

**EF Core:** the entry table is opt-in and lives in your own context, so your migrations create it. The bundled
`DynamicEndpointsDbContext` doesn't contain it.

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder) =>
    modelBuilder.ApplyDynamicEndpointsConfiguration()
                .ApplyDynamicEndpointsAuditConfiguration();   // table "DynamicEndpointAudit"
```

Filterable fields (timestamp, endpoint, tenant, user, method, route) are columns and the complete entry is JSON. Entries are saved
with their own `SaveChanges`, after the change was applied, so they are not part of a [change set](change-sets.md)'s transaction.

## Querying

With a queryable sink, the admin API serves the log, newest first:

| Method | Path | |
|---|---|---|
| `GET` | `/audit?endpointId=&tenant=&user=&from=&to=&limit=` | entries (`limit`: default 100, at most 1000) |
| `GET` | `/{id}/audit` | entries of one endpoint |

Without one, both return `404`. A tenant's admin API (`MapDynamicEndpointsTenantAdmin`) only returns that tenant's entries. In code,
inject `IDynamicEndpointAuditLog` and call `QueryAsync(new DynamicEndpointAuditQuery { … })`.
