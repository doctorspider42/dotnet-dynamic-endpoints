# 09 – Drafts, history & audit log

A change doesn't have to go live right away: save it as a **draft** – validated like any definition, but routing keeps serving
the published revision – and publish it by hand or at a set time. Every change is a **revision**: diff any two, roll back with
one call (as a new revision, history is never rewritten). The **audit log** records who made each change, with a
property-by-property diff – to the log and to a table of your context.

| File | What to look at |
|---|---|
| [`DraftsSeeder.cs`](DraftsSeeder.cs) | `CreateAsync` → `UpdateAsync` (revisions 1 and 2), `SaveDraftAsync(…, comment:)` on top, and a new endpoint drafted with `publishAt:` |
| [`Program.cs`](Program.cs) | `ScheduledPublishInterval`, `AddAuditLog(a => a.ToLogger().ToEntityFramework<AppDbContext>().Configure(…))` with the user from `X-User` |
| [`AppDbContext.cs`](AppDbContext.cs) | `ApplyDynamicEndpointsConfiguration()` (definitions, revisions, drafts) and `ApplyDynamicEndpointsAuditConfiguration()` |
| [`drafts-history-audit.http`](drafts-history-audit.http) | the requests below |

The seeded state: `GET /prices/{sku}` (id `0199c0de-0000-7000-8000-000000000001`) is at revision 2 (price 12) and has a draft
with price 15 and a new `currency` parameter. `GET /promo` exists only as a draft, scheduled two minutes after the first start.

## Run it

```bash
dotnet run --project samples/09-DraftsHistoryAudit
```

Definitions, revisions, drafts and the audit table in `drafts-history-audit.db` (in the sample's folder, the working directory of
`dotnet run`). Delete it to get the seeded history back. Panel: <http://localhost:5109/admin/>, Swagger UI:
<http://localhost:5109/swagger>.

## Click around

- The list shows *Get price* with a draft badge and *Promotion* as a scheduled draft. Wait two minutes: *Promotion* is
  published by the scheduler and `GET /promo` answers.
- Open *Get price* → the editor has the draft; *Save draft* (with a publish time and a comment) or *Save & publish*.
- *History* on *Get price*: revisions with their kind and comment, a diff against the previous or the published one, and
  *Roll back*.
- *Audit log* (toolbar above the list): who changed what – filter by endpoint, user, time. Changes made in the panel have no
  user here (no authentication); send `X-User` through the admin API to see one.

## Call it

```bash
curl http://localhost:5109/prices/ANV-1                     # {"sku":"ANV-1","price":12,"currency":"EUR"} – revision 2
curl http://localhost:5109/api/admin/endpoints/drafts       # the drafts, with publishAt and comment
curl http://localhost:5109/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001/draft/diff   # what publishing would change

curl -X POST -H "X-User: alice" http://localhost:5109/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001/publish
curl "http://localhost:5109/prices/ANV-1?currency=PLN"      # price 15, currency PLN – revision 3

curl http://localhost:5109/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001/revisions    # Published, Updated, Created
curl "http://localhost:5109/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001/diff?from=1&to=2"
# [{"path":"parameters[sku].maxLength","kind":"Changed","from":10,"to":20}, …]

curl -X POST -H "X-User: bob" http://localhost:5109/api/admin/endpoints/0199c0de-0000-7000-8000-000000000001/revisions/1/rollback
curl http://localhost:5109/prices/ANV-1                     # price 10 again – revision 4, kind RolledBack, sourceRevision 1

curl http://localhost:5109/api/admin/endpoints/audit        # who did what: alice published, bob rolled back
```

## Read more

- [Drafts, history & rollback](../../docs/articles/drafts-and-history.md) · [Audit log](../../docs/articles/audit-log.md) ·
  [Change events](../../docs/articles/change-events.md) · [Change sets](../../docs/articles/change-sets.md)
- [Admin REST API](../../docs/articles/admin-api.md) · [Admin panel](../../docs/articles/admin-ui.md) · [EF Core & migrations](../../docs/articles/ef-core.md)
- Next: [10 – Caching & rate limits](../10-CachingAndRateLimits/README.md), or the [overview of all samples](../../docs/articles/samples.md).
