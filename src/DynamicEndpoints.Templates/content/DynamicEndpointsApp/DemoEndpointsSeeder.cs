using DynamicEndpoints;
using DynamicEndpointsApp.Processors;

namespace DynamicEndpointsApp;

/// <summary>
/// Creates demo endpoints on first start, built with the fluent API. Found by <c>AddFromAssemblyContaining&lt;Program&gt;()</c>.
/// Change or delete them at runtime through the admin API – or remove this class.
/// </summary>
public sealed class DemoEndpointsSeeder(IDynamicEndpointManager manager, ILogger<DemoEndpointsSeeder> logger) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return; // already seeded – the definitions are persisted in the database
        }

        DynamicEndpointDefinition[] definitions =
        [
            DynamicEndpoint.Get("/hello/{name}")
                .Named("Say hello")
                .InGroup("Demo")
                .HandledBy<GreetingProcessor, GreetingConfig>(new() { Greeting = "Hello" })
                .FromRoute("name", p => p.String().Length(2, 30).Description("Who to greet")),

            DynamicEndpoint.Get("/greetings")
                .Named("Greet loudly")
                .WithDescription("Same processor, different configuration – and a query parameter with a default.")
                .InGroup("Demo")
                .HandledBy<GreetingProcessor, GreetingConfig>(new() { Greeting = "Good morning", Shout = true })
                .FromQuery("name", p => p.String().MaxLength(30).Default("World").Example("Ada")),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }

        logger.LogInformation("Seeded {Count} demo endpoints.", definitions.Length);
    }
}
