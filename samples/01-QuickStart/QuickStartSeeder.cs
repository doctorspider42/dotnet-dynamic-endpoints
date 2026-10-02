namespace DynamicEndpoints.Samples.QuickStart;

/// <summary>
/// Creates two endpoints on the first start, with the fluent builder. Seeders run when the application starts, after the stored
/// definitions were loaded; <c>AddFromAssemblyContaining&lt;Program&gt;()</c> finds this one. It only seeds an empty store, so
/// what you change in the panel survives a restart.
/// </summary>
public sealed class QuickStartSeeder(IDynamicEndpointManager manager, ILogger<QuickStartSeeder> logger) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return; // seeded before – the definitions are in quickstart.db
        }

        DynamicEndpointDefinition[] definitions =
        [
            // GET /hello/Ada → { "message": "Hello, Ada!" }; GET /hello/A → 400, the name is too short.
            DynamicEndpoint.Get("/hello/{name}")
                .Named("Say hello")
                .InGroup("Quick start")
                .HandledBy<GreetingProcessor, GreetingConfig>(new() { Greeting = "Hello" })
                .FromRoute("name", p => p.String().Length(2, 30).Description("Who to greet").Example("Ada")),

            // The same processor with another configuration, and a query parameter with a default.
            DynamicEndpoint.Get("/greetings")
                .Named("Greet loudly")
                .WithDescription("Same processor, different configuration – and a query parameter with a default.")
                .InGroup("Quick start")
                .HandledBy<GreetingProcessor, GreetingConfig>(new() { Greeting = "Good morning", Shout = true })
                .FromQuery("name", p => p.String().MaxLength(30).Default("World").Example("Ada")),
        ];

        foreach (var definition in definitions)
        {
            // Validated, saved and routable right away.
            await manager.CreateAsync(definition, cancellationToken);
        }

        logger.LogInformation("Seeded {Count} endpoints.", definitions.Length);
    }
}
