using System.ComponentModel.DataAnnotations;

namespace DynamicEndpoints.Samples.QuickStart;

/// <summary>
/// The configuration of one endpoint that uses the "greeting" processor. It is stored with the endpoint definition, so admins
/// set it per endpoint (as JSON in the panel); it is deserialized strictly and checked with DataAnnotations when saved.
/// </summary>
public sealed class GreetingConfig
{
    [Required(ErrorMessage = "'greeting' is required.")]
    [MaxLength(50)]
    public string? Greeting { get; set; } = "Hello";

    public bool Shout { get; set; }
}

/// <summary>
/// A building block for dynamic endpoints: it gets the validated parameters and the endpoint's typed configuration. Processors
/// are regular DI services, so inject your repositories, HTTP clients, … in the constructor. The attribute names it for the
/// admins and the panel; <c>AddFromAssemblyContaining&lt;Program&gt;()</c> registers it.
/// </summary>
[DynamicProcessor("greeting",
    Description = "Greets the 'name' parameter.",
    ConfigurationExample = """{ "greeting": "Hello", "shout": false }""")]
public sealed class GreetingProcessor(TimeProvider timeProvider) : DynamicEndpointProcessor<GreetingConfig>
{
    protected override Task<IResult> ProcessAsync(DynamicRequest request, GreetingConfig configuration)
    {
        // Parameters are bound, converted and validated already – a missing optional one is simply absent.
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
