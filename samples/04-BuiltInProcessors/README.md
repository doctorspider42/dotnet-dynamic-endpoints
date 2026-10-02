# 04 – Built-in processors

`http-forward`, `webhook`, `response` and `sql-query`: endpoints without a line of processor code, only configuration – typed,
validated on save, and with a form of its own in the panel. Self-contained: the services the forwards and webhooks talk to are
fake ones mapped in the same app under the reserved `/fake` prefix ([`FakeUpstream.cs`](FakeUpstream.cs)), so it works offline.

| Endpoint | Processor | Shows |
|---|---|---|
| `GET /customers/{id}` | `http-forward` | URL template (`{id}` is URL-encoded), an API key from the configuration (`{config:Upstream:ApiKey}`), a response template over the upstream body (`{{response.fullName}}`, `{{status}}`) |
| `GET /customers/{id}/raw` | `http-forward` | the upstream response relayed as it is – status code included (`101` → `404`) |
| `POST /orders` | `webhook` | a payload template, HMAC-SHA256 signature (`X-Webhook-Signature`, secret from `Webhooks:SigningSecret`), `X-Webhook-Delivery` id, `202` |
| `POST /orders/flaky` | `webhook` | retries with back-off: the receiver answers `503` to the first attempt, the answer says `"attempts": 2` |
| `GET /status`, `POST /quotes`, `GET /motd` | `response` | a fixed JSON body with a header, a request → `201` response mapping (numbers stay numbers), a text response |
| `GET /reports/products?minPrice=`, `GET /reports/products/{sku}`, `GET /reports/stock-value` | `sql-query` | a parameterized, read-only `SELECT` with `Rows`, `Row` (or `404`) and `Value` results |

| File | What to look at |
|---|---|
| [`Program.cs`](Program.cs) | `AddBuiltInProcessors(o => o.AllowedHosts.Add(…))` (SSRF guard), `AddSqlQueryProcessor(…)` with a read-only connection, the named `HttpClient` |
| [`BuiltInProcessorsSeeder.cs`](BuiltInProcessorsSeeder.cs) | the configurations, typed: `HttpForwardConfig`, `WebhookConfig`, `ResponseTemplateConfig`, `SqlQueryConfig` |
| [`FakeUpstream.cs`](FakeUpstream.cs) | the fake CRM and webhook receivers – they check the API key and the signature |
| [`appsettings.json`](appsettings.json) | `Upstream:BaseUrl` (this app's own address), the CRM API key and the webhook secret – secrets stay out of definitions |
| [`built-in-processors.http`](built-in-processors.http) | the requests below |

## Run it

```bash
dotnet run --project samples/04-BuiltInProcessors
```

Definitions and the products table in `built-in-processors.db` (in the sample's folder, the working directory of `dotnet run`). Panel: <http://localhost:5104/admin/>,
Swagger UI: <http://localhost:5104/swagger>. If you run it on another port, change `Upstream:BaseUrl` too
(`dotnet run --project samples/04-BuiltInProcessors -- --urls http://localhost:6000 --Upstream:BaseUrl=http://localhost:6000`) – and
delete the database, the URLs are seeded into the definitions.

## Click around

- Open *Get customer (mapped)*: the configuration is a form (URL, headers, response template, timeout) with a *Form / JSON* toggle.
- Create an `http-forward` endpoint to `https://example.com/` – saving fails, the host isn't in `AllowedHosts`.
- Open *Products report*: change the query to `DELETE FROM "Products"` – rejected on save, only `SELECT`/`WITH`/`VALUES` are allowed.
- Call *Place order (webhook)* in the *Try* console, then look at <http://localhost:5104/fake/webhooks>: the payload, the
  delivery id and `"signatureValid": true`.

## Call it

```bash
curl http://localhost:5104/customers/42          # {"id":42,"name":"Customer 42","tier":"gold","upstreamStatus":200}
curl -i http://localhost:5104/customers/101/raw  # 404, relayed from the fake CRM

curl -X POST http://localhost:5104/orders -H "Content-Type: application/json" -d '{"sku":"ANV-1","quantity":2}'
# 202 {"deliveryId":"…","delivered":true,"attempts":1,"status":200}
curl -X POST http://localhost:5104/orders/flaky -H "Content-Type: application/json" -d '{"sku":"ROC-2"}'      # "attempts":2
curl http://localhost:5104/fake/webhooks         # what the receivers got, with "signatureValid": true

curl -i http://localhost:5104/status             # X-Mock: true
curl -X POST http://localhost:5104/quotes -H "Content-Type: application/json" -d '{"sku":"MAG-3","quantity":3}'  # 201
curl "http://localhost:5104/motd?name=Ada"       # text/plain

curl "http://localhost:5104/reports/products?minPrice=20"   # rows
curl http://localhost:5104/reports/products/MAG-3           # one row (or 404)
curl http://localhost:5104/reports/stock-value              # 1785.4
```

## Read more

- [Built-in processors](../../docs/articles/built-in-processors.md) – every configuration property, templates, secrets, SSRF,
  the SQL safety net
- [Security](../../docs/articles/security.md) · [Admin panel](../../docs/articles/admin-ui.md) (processor forms)
- Next: [05 – CRUD on EF Core entities](../05-EfCrud/README.md), or the [overview of all samples](../../docs/articles/samples.md).
