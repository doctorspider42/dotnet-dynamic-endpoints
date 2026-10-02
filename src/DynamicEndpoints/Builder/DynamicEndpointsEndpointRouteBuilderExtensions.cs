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

        MapRevisions(group, root);
        return group;
    }

    // Drafts, history, diffs and rollback. A store without IDynamicEndpointRevisionStore answers 501.
    private static void MapRevisions(RouteGroupBuilder group, string root)
    {
        group.MapGet("/drafts", (IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.ListDraftsAsync(ct))))
            .WithSummary("Lists all unpublished drafts.")
            .Produces<IReadOnlyList<DynamicEndpointDraft>>()
            .ProducesProblem(StatusCodes.Status501NotImplemented);

        group.MapPost("/drafts", (DynamicEndpointDraft draft, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () =>
                {
                    var saved = await manager.SaveDraftAsync(draft, ct);
                    return TypedResults.Created($"{root}/{saved.EndpointId}/draft", saved);
                }))
            .WithSummary("Saves a draft of a new endpoint (or, with 'definition.id', of an existing one). Nothing is routed until it is published.")
            .Produces<DynamicEndpointDraft>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}/draft", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => await manager.GetDraftAsync(id, ct) is { } draft ? TypedResults.Ok(draft) : TypedResults.NotFound()))
            .WithSummary("Gets the draft of an endpoint.")
            .Produces<DynamicEndpointDraft>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/draft", (Guid id, DynamicEndpointDraft draft, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.SaveDraftAsync(draft with { Definition = draft.Definition with { Id = id } }, ct))))
            .WithSummary("Saves the draft of an endpoint. 'definition.revision' is the revision it is based on (0: the current one); 'publishAt' schedules it.")
            .Produces<DynamicEndpointDraft>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapDelete("/{id:guid}/draft", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => await manager.DiscardDraftAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound()))
            .WithSummary("Discards the draft of an endpoint; the published revision stays.");

        group.MapGet("/{id:guid}/draft/diff", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.DiffDraftAsync(id, ct))))
            .WithSummary("What publishing the draft would change.")
            .Produces<IReadOnlyList<DynamicEndpointDifference>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/publish", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.PublishAsync(id, ct))))
            .WithSummary("Publishes the draft of an endpoint as its next revision.")
            .Produces<DynamicEndpointDefinition>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{id:guid}/revisions", (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.GetHistoryAsync(id, ct))))
            .WithSummary("Lists the revisions of an endpoint, newest first.")
            .Produces<IReadOnlyList<DynamicEndpointRevision>>();

        group.MapGet("/{id:guid}/revisions/{revision:int}", (Guid id, int revision, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => await manager.GetRevisionAsync(id, revision, ct) is { } r ? TypedResults.Ok(r) : TypedResults.NotFound()))
            .WithSummary("Gets one revision of an endpoint.")
            .Produces<DynamicEndpointRevision>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/revisions/{revision:int}/rollback", (Guid id, int revision, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () => TypedResults.Ok(await manager.RollbackAsync(id, revision, ct))))
            .WithSummary("Publishes the content of an earlier revision again, as the next revision.")
            .Produces<DynamicEndpointDefinition>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{id:guid}/diff", (Guid id, int from, int? to, IDynamicEndpointManager manager, CancellationToken ct) =>
                Guard(async () =>
                {
                    var target = to ?? (await manager.GetAsync(id, ct))?.Definition.Revision ?? throw new DynamicEndpointNotFoundException(id);
                    return TypedResults.Ok(await manager.DiffAsync(id, from, target, ct));
                }))
            .WithSummary("What changed from revision 'from' to revision 'to' (default: the published one).")
            .Produces<IReadOnlyList<DynamicEndpointDifference>>()
            .ProducesProblem(StatusCodes.Status404NotFound);
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
        catch (NotSupportedException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
    }
}
