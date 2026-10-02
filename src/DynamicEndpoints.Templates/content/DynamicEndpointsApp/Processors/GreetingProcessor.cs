using System.ComponentModel.DataAnnotations;
using DynamicEndpoints;

namespace DynamicEndpointsApp.Processors;

/// <summary>Configuration of one endpoint that uses the "greeting" processor – stored with the endpoint definition.</summary>
public sealed class GreetingConfig
{
    [Required(ErrorMessage = "'greeting' is required.")]
    public string? Greeting { get; set; } = "Hello";

    public bool Shout { get; set; }
}

/// <summary>
/// A building block for dynamic endpoints: gets validated parameters and the endpoint's typed configuration.
/// Processors are regular DI services – inject your repositories, HTTP clients, … in the constructor.
/// </summary>
[DynamicProcessor("greeting",
    Description = "Greets the 'name' parameter.",
    ConfigurationExample = """{ "greeting": "Hello", "shout": false }""")]
public sealed class GreetingProcessor(TimeProvider timeProvider) : DynamicEndpointProcessor<GreetingConfig>
{
    protected override Task<IResult> ProcessAsync(DynamicRequest request, GreetingConfig configuration)
    {
        var name = request.Get<string>("name") ?? "World";
        var message = $"{configuration.Greeting}, {name}!";

        return Task.FromResult(Results.Ok(new
        {
            message = configuration.Shout ? message.ToUpperInvariant() : message,
            endpoint = request.Endpoint.Name,
            at = timeProvider.GetUtcNow(),
        }));
    }
}
