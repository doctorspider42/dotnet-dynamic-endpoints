using System.Text.Json;
using DynamicEndpoints;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

        group.MapGet("/", (IDynamicEndpointManager manager, string? tenant, CancellationToken ct) => ListAsync(manager, tenant, ct))
            .WithSummary("Lists all endpoint definitions with their runtime status – optionally only those of one tenant.");

        group.MapGet("/tenants", async (IDynamicEndpointManager manager, CancellationToken ct) =>
                (await manager.ListAsync(ct)).Select(s => s.Definition.Tenant).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList())
            .WithSummary("Lists the tenants that own endpoints.");

        group.MapGet("/processors", (IDynamicEndpointManager manager) => manager.Processors)
            .WithSummary("Lists processors that can handle dynamic endpoints.");

        group.MapGet("/validators", (IDynamicEndpointManager manager) => manager.Validators)
            .WithSummary("Lists custom validators that can be attached to parameters and endpoints.");

        group.MapGet("/{id:guid}", async Task<Results<Ok<DynamicEndpointState>, NotFound>> (Guid id, IDynamicEndpointManager manager, CancellationToken ct) =>
                await manager.GetAsync(id, ct) is { } state ? TypedResults.Ok(state) : TypedResults.NotFound())
            .WithSummary("Gets a single endpoint definition.");

        group.MapPost("/", (DynamicEndpointDefinition definition, IDynamicEndpointManager manager, HttpContext http, CancellationToken ct) =>
                Guard(async () =>
                {
                    var created = await manager.CreateAsync(definition, ct);
                    // Prefixes with parameters (a tenant's admin API) take the actual path.
                    var location = root.Contains('{') ? http.Request.Path.Value!.TrimEnd('/') : root;
                    return TypedResults.Created($"{location}/{created.Id}", created);
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

    /// <summary>
    /// Maps the management REST API of a single tenant: it sees and changes only that tenant's endpoints, and new ones are assigned
    /// to it. The tenant comes from the route parameter of <paramref name="prefix"/> (e.g. <c>/admin/tenants/{tenant}/endpoints</c>),
    /// or from the resolvers of <c>UseMultiTenancy()</c> when the prefix has none. Secure it with a policy that checks the user
    /// belongs to the tenant.
    /// </summary>
    public static RouteGroupBuilder MapDynamicEndpointsTenantAdmin(this IEndpointRouteBuilder endpoints, string prefix = "/_dynamic-endpoints/tenants/{tenant}")
    {
        var routeParameter = RoutePatternFactory.Parse(prefix).Parameters.FirstOrDefault()?.Name;
        if (prefix.IndexOf('{') is > 0 and var literal)
        {
            endpoints.ServiceProvider.GetRequiredService<RouteInspector>().Reserve(prefix[..literal]);
        }

        var group = endpoints.MapDynamicEndpointsAdmin(prefix).WithTags("Dynamic endpoints administration (tenant)");
        group.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var tenant = routeParameter is not null && http.Request.RouteValues.TryGetValue(routeParameter, out var value)
                ? value?.ToString()
                : await http.GetDynamicEndpointTenantAsync();
            if (!DynamicEndpointsTenancyOptions.IsValidTenant(tenant))
            {
                return TypedResults.Problem("The request has no valid tenant.", statusCode: StatusCodes.Status404NotFound);
            }

            // Every handler works on the tenant's view of the manager.
            for (var i = 0; i < context.Arguments.Count; i++)
            {
                if (context.Arguments[i] is IDynamicEndpointManager manager)
                {
                    context.Arguments[i] = manager.ForTenant(tenant);
                }
            }

            http.Items[AdminTenantKey] = tenant;
            return await next(context);
        });
        return group;
    }

    /// <summary>
    /// Serves the OpenAPI 3.1 document of the active dynamic endpoints – point Swagger UI / Scalar at it. With multi-tenancy it is
    /// the document of the request's tenant: from a <c>{tenant}</c> route parameter (<c>/openapi/{tenant}/dynamic.json</c>) or the
    /// tenant resolvers; without a tenant it lists the shared endpoints.
    /// </summary>
    public static IEndpointConventionBuilder MapDynamicEndpointsOpenApi(this IEndpointRouteBuilder endpoints, string pattern = "/openapi/dynamic-endpoints.json") =>
        endpoints.MapGet(pattern, async (HttpContext http, IDynamicOpenApiDocumentProvider provider) =>
            {
                var tenant = http.Request.RouteValues.TryGetValue("tenant", out var value) && value?.ToString() is { Length: > 0 } fromRoute
                    ? fromRoute
                    : await http.GetDynamicEndpointTenantAsync();
                var document = tenant is null ? provider.GetDocument() : provider.GetDocument(tenant);
                return Results.Text(document.ToJsonString(IndentedJson), "application/json");
            })
            .ExcludeFromDescription();

    // Tenant of a tenant admin API request, for handlers that don't go through the manager.
    internal const string AdminTenantKey = "DynamicEndpoints.AdminTenant";

    private static async Task<IReadOnlyList<DynamicEndpointState>> ListAsync(IDynamicEndpointManager manager, string? tenant, CancellationToken ct) =>
        tenant is null ? await manager.ListAsync(ct)
        : DynamicEndpointsTenancyOptions.IsValidTenant(tenant) ? await manager.ForTenant(tenant).ListAsync(ct)
        : [];

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
