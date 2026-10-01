namespace DynamicEndpoints.Sample.Processors;

[DynamicProcessor("echo", Description = "Returns the validated, normalized parameters – handy for testing definitions.")]
public sealed class EchoProcessor(TimeProvider timeProvider) : IDynamicEndpointProcessor
{
    public Task<IResult> ProcessAsync(DynamicRequest request) => Task.FromResult(Results.Ok(new
    {
        endpoint = new { request.Endpoint.Id, request.Endpoint.Name, request.Endpoint.Revision },
        parameters = request.Parameters,
        receivedAt = timeProvider.GetUtcNow(),
    }));
}
