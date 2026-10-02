using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.Observability;

/// <summary>
/// One endpoint per outcome you'd want to see on a dashboard: processed, rejected by a validation layer, slow, failing.
/// Every measurement and span carries the endpoint's id, name, processor, route and method.
/// </summary>
public sealed class ObservabilitySeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        DynamicEndpointDefinition[] definitions =
        [
            // outcome "processed"
            DynamicEndpoint.Get("/hello/{name}")
                .Named("Say hello")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["message"] = "Hello, {{name}}!" } })
                .FromRoute("name", p => p.String().Length(2, 30)),

            // outcome "rejected": constraints fail, or – when they pass – the rule; validation.failures by layer, one span per layer
            DynamicEndpoint.Post("/orders")
                .Named("Create order")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { StatusCode = 201, Body = new JsonObject { ["sku"] = "{{sku}}", ["quantity"] = "{{quantity}}" } })
                .FromBody("sku", p => p.String().Required().Pattern("^[A-Z]{3}-[0-9]{1,4}$"))
                .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
                .WithRule("""{ "<=": [{ "var": "quantity" }, 10] }""", "At most 10 of a kind.", "quantity"),

            // processor.duration: the "work" processor sleeps ?ms=
            DynamicEndpoint.Get("/work")
                .Named("Slow work")
                .HandledBy("work")
                .FromQuery("ms", p => p.Integer().Range(0, 5000).Default(250)),

            // outcome "error", the errors counter with error.type, a span with status Error – and a 500 for the client
            DynamicEndpoint.Get("/boom")
                .Named("Always fails")
                .HandledBy("boom"),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
