using System.Text.Json;
using DynamicEndpoints;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.AspNetCore.Builder;

public static class DynamicEndpointsEndpointRouteBuilderExtensions
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    /// <summary>
    /// Plugs the dynamic endpoints into routing. Must be called on the application itself (not on a route group).
    /// The returned builder applies conventions to every dynamic endpoint, e.g. <c>.RequireRateLimiting("api")</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapDynamicEndpoints(this IEndpointRouteBuilder endpoints)
    {
        if (endpoints is RouteGroupBuilder)
        {
            throw new InvalidOperationException("MapDynamicEndpoints must be called on the application, not on a route group.");
        }

        var services = endpoints.ServiceProvider;
        var dataSource = services.GetService<DynamicEndpointDataSource>()
            ?? throw new InvalidOperationException("Call services.AddDynamicEndpoints() before MapDynamicEndpoints().");
        var inspector = services.GetRequiredService<RouteInspector>();
        if (inspector.IsAttached)
        {
            throw new InvalidOperationException("MapDynamicEndpoints can only be called once.");
        }

        inspector.Attach(endpoints.DataSources, dataSource);
        endpoints.DataSources.Add(dataSource);
        return services.GetRequiredService<DynamicEndpointConventions>();
    }

    /// <summary>
    /// Maps the management REST API under <paramref name="prefix"/> and reserves the prefix.
    /// Secure it, e.g. <c>app.MapDynamicEndpointsAdmin().RequireAuthorization("admin")</c>.
    /// </summary>
    public static RouteGroupBuilder MapDynamicEndpointsAdmin(this IEndpointRouteBuilder endpoints, string prefix = "/_dynamic-endpoints")
    {
        endpoints.ServiceProvider.GetRequiredService<RouteInspector>().Reserve(prefix);
        var root = prefix.TrimEnd('/');
        var group = endpoints.MapGroup(prefix).WithTags("Dynamic endpoints administration");

        group.MapGet("/", (IDynamicEndpointManager manager, CancellationToken ct) => manager.ListAsync(ct))
            .WithSummary("Lists all endpoint definitions with their runtime status.");

        group.MapGet("/processors", (IDynamicEndpointManager manager) => manager.Processors)
            .WithSummary("Lists processors that can handle dynamic endpoints.");

        group.MapGet("/validators", (IDynamicEndpointManager manager) => manager.Validators)
            .WithSummary("Lists custom validators that can be attached to parameters and endpoints.");

        group.MapGet("/{id:guid}", async Task<Results<Ok<DynamicEndpointState>, NotFound>> (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                await manager.GetAsync(id, ct) is { } state ? TypedResults.Ok(state) : TypedResults.NotFound())
            .WithSummary("Gets a single endpoint definition.");

        group.MapPost("/", (DynamicEndpointDefinition definition, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () =>
                {
                    var created = await manager.CreateAsync(definition, ct);
                    return TypedResults.Created($"{root}/{created.Id}", created);
                }))
            .WithSummary("Creates and publishes an endpoint.")
            .Produces<DynamicEndpointDefinition>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapPut("/{id:guid}", (Guid id, DynamicEndpointDefinition definition, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.UpdateAsync(definition with { Id = id }, ct))))
            .WithSummary("Replaces an endpoint definition. 'revision' must match the stored revision.")
            .Produces<DynamicEndpointDefinition>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound>> (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                await manager.DeleteAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound())
            .WithSummary("Deletes an endpoint.");

        group.MapPost("/{id:guid}/enable", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.SetEnabledAsync(id, true, ct))))
            .WithSummary("Enables an endpoint.")
            .Produces<DynamicEndpointDefinition>()
            .ProducesValidationProblem();

        group.MapPost("/{id:guid}/disable", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.SetEnabledAsync(id, false, ct))))
            .WithSummary("Disables an endpoint without deleting it.")
            .Produces<DynamicEndpointDefinition>();

        group.MapPost("/validate", (DynamicEndpointDefinition definition, IDynamicEndpointManager manager, CancellationToken ct) =>
                manager.ValidateAsync(definition, ct))
            .WithSummary("Validates a definition without saving it.");

        group.MapPost("/reload", async (IDynamicEndpointManager manager, CancellationToken ct) =>
            {
                await manager.ReloadAsync(ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Re-reads all definitions from the store.");

        return group;
    }

    /// <summary>Serves the OpenAPI 3.1 document of the active dynamic endpoints – point Swagger UI / Scalar at it.</summary>
    public static IEndpointConventionBuilder MapDynamicEndpointsOpenApi(this IEndpointRouteBuilder endpoints, string pattern = "/openapi/dynamic-endpoints.json") =>
        endpoints.MapGet(pattern, (IDynamicOpenApiDocumentProvider provider) =>
                Results.Text(provider.GetDocument().ToJsonString(IndentedJson), "application/json"))
            .ExcludeFromDescription();

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (DynamicEndpointValidationException ex)
        {
            return TypedResults.ValidationProblem(ex.Errors, title: "The endpoint definition is invalid.");
        }
        catch (DynamicEndpointNotFoundException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (DynamicEndpointConcurrencyException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
