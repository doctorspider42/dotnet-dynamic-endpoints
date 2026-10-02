using System.Text.Json.Nodes;
using DynamicEndpoints.Sql;

namespace DynamicEndpoints.Samples.BuiltInProcessors;

/// <summary>
/// Endpoints of the four built-in processors – no code of our own behind any of them, only configuration. In the panel every
/// one of them has a form for its configuration. The upstream base URL comes from the configuration (Upstream:BaseUrl), so the
/// forward and webhook targets are the fake services of this very app (FakeUpstream.cs).
/// </summary>
public sealed class BuiltInProcessorsSeeder(IDynamicEndpointManager manager, IConfiguration configuration) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        var upstream = configuration["Upstream:BaseUrl"]!.TrimEnd('/');

        DynamicEndpointDefinition[] definitions =
        [
            // http-forward: calls another service through IHttpClientFactory. {id} is URL-encoded into the path, the API key comes
            // from the configuration (never stored in the definition), and the response is mapped by a template.
            DynamicEndpoint.Get("/customers/{id}")
                .Named("Get customer (mapped)")
                .InGroup("http-forward")
                .HandledBy<HttpForwardProcessor, HttpForwardConfig>(new()
                {
                    Url = upstream + "/fake/crm/customers/{id}",
                    Headers = new() { ["X-Api-Key"] = "{config:Upstream:ApiKey}" },
                    ResponseTemplate = new JsonObject
                    {
                        ["id"] = "{{id}}",                       // a lone placeholder keeps the JSON type: a number
                        ["name"] = "{{response.fullName}}",      // the parsed upstream body
                        ["tier"] = "{{response.tier}}",
                        ["upstreamStatus"] = "{{status}}",
                    },
                })
                .FromRoute("id", p => p.Integer().Range(1, 1000).Example(42)),

            // …or relayed as it is, status code included (try id 101 for the upstream's 404).
            DynamicEndpoint.Get("/customers/{id}/raw")
                .Named("Get customer (relayed)")
                .InGroup("http-forward")
                .HandledBy<HttpForwardProcessor, HttpForwardConfig>(new()
                {
                    Url = upstream + "/fake/crm/customers/{id}",
                    Headers = new() { ["X-Api-Key"] = "{config:Upstream:ApiKey}" },
                })
                .FromRoute("id", p => p.Integer().Range(1, 1000).Example(101)),

            // webhook: a JSON payload built from a template, signed with HMAC-SHA256 (the secret is a configuration key),
            // delivered with retries; 202 once the receiver accepted it.
            DynamicEndpoint.Post("/orders")
                .Named("Place order (webhook)")
                .InGroup("webhook")
                .HandledBy<WebhookProcessor, WebhookConfig>(new()
                {
                    Url = upstream + "/fake/webhooks",
                    Payload = new JsonObject { ["event"] = "order.created", ["sku"] = "{{sku}}", ["quantity"] = "{{quantity}}" },
                    SigningSecretConfigurationKey = "Webhooks:SigningSecret",
                })
                .FromBody("sku", p => p.String().Required().Pattern("^[A-Z]{3}-[0-9]{1,4}$").Example("ANV-1"))
                .FromBody("quantity", p => p.Integer().Required().Range(1, 100).Example(2)),

            // The receiver answers 503 to the first attempt – the second one succeeds ("attempts": 2).
            DynamicEndpoint.Post("/orders/flaky")
                .Named("Place order (flaky receiver)")
                .InGroup("webhook")
                .HandledBy<WebhookProcessor, WebhookConfig>(new()
                {
                    Url = upstream + "/fake/webhooks/flaky",
                    SigningSecretConfigurationKey = "Webhooks:SigningSecret",
                    Retries = 2,
                    RetryDelayMilliseconds = 200,
                })
                .FromBody("sku", p => p.String().Required().Example("ROC-2")),

            // response: fixed answers and mocks, with {{placeholders}} from the request.
            DynamicEndpoint.Get("/status")
                .Named("Status")
                .InGroup("response")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new()
                {
                    Body = new JsonObject { ["status"] = "ok", ["service"] = "built-in processors sample" },
                    Headers = new() { ["X-Mock"] = "true" },
                }),

            DynamicEndpoint.Post("/quotes")
                .Named("Quote (request → response)")
                .WithDescription("Maps the request to a 201 response; 'quantity' stays a number.")
                .InGroup("response")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new()
                {
                    StatusCode = 201,
                    Body = new JsonObject { ["sku"] = "{{sku}}", ["quantity"] = "{{quantity}}", ["text"] = "{{quantity}} × {{sku}}" },
                })
                .FromBody("sku", p => p.String().Required().Example("MAG-3"))
                .FromBody("quantity", p => p.Integer().Required().Range(1, 100).Example(3)),

            DynamicEndpoint.Get("/motd")
                .Named("Message of the day (text)")
                .InGroup("response")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Text = "Hello {{name}}, have a nice day.", ContentType = "text/plain; charset=utf-8" })
                .FromQuery("name", p => p.String().MaxLength(30).Default("stranger")),

            // sql-query: a read-only, parameterized SELECT over the products table – @minPrice is bound as a parameter, never
            // concatenated. Missing parameters are NULL.
            DynamicEndpoint.Get("/reports/products")
                .Named("Products report")
                .InGroup("sql-query")
                .HandledBy<SqlQueryProcessor, SqlQueryConfig>(new()
                {
                    Query = """SELECT "Sku" AS sku, "Name" AS name, "Price" AS price, "Stock" AS stock FROM "Products" WHERE @minPrice IS NULL OR "Price" >= @minPrice ORDER BY "Price" DESC""",
                    Result = SqlQueryResult.Rows,
                    MaxRows = 50,
                })
                .FromQuery("minPrice", p => p.Number().Min(0).Example(20)),

            DynamicEndpoint.Get("/reports/products/{sku}")
                .Named("One product (SQL)")
                .InGroup("sql-query")
                .HandledBy<SqlQueryProcessor, SqlQueryConfig>(new()
                {
                    Query = """SELECT "Sku" AS sku, "Name" AS name, "Price" AS price, "Stock" AS stock FROM "Products" WHERE "Sku" = @sku""",
                    Result = SqlQueryResult.Row,     // the first row, or 404
                })
                .FromRoute("sku", p => p.String().Pattern("^[A-Z]{3}-[0-9]{1,4}$").Example("ANV-1")),

            DynamicEndpoint.Get("/reports/stock-value")
                .Named("Stock value (SQL)")
                .InGroup("sql-query")
                .HandledBy<SqlQueryProcessor, SqlQueryConfig>(new()
                {
                    Query = """SELECT ROUND(SUM("Price" * "Stock"), 2) FROM "Products" """,
                    Result = SqlQueryResult.Value,   // the first column of the first row
                }),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
