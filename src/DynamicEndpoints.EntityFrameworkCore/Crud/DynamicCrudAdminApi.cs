using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// <c>GET /crud/entities</c> and <c>POST /scaffold/crud</c> in every admin API. In a tenant's admin API both see only the entities
/// its tenant may use, and the scaffolder works on the tenant's view of the transfer (its tenant, its routes).
/// </summary>
internal sealed class DynamicCrudAdminApi(DynamicCrudCatalog catalog) : IDynamicEndpointsAdminApiExtension
{
    public void Map(RouteGroupBuilder group)
    {
        group.MapGet("/crud/entities", (HttpContext http) =>
                TypedResults.Ok(catalog.AvailableTo(AdminTenant(http)).Select(DynamicCrudScaffolder.Describe).ToList()))
            .WithSummary("Lists the entities the ef-crud processor may use (DynamicEndpoints.EntityFrameworkCore), with their fields and operations.")
            .Produces<IReadOnlyList<DynamicCrudEntityInfo>>();

        // The transfer argument is replaced by the tenant's view in a tenant admin API (like the OpenAPI import's).
        group.MapPost("/scaffold/crud", (HttpContext http, string? entity, bool? dryRun, string? routePrefix, string? group,
                [FromQuery] string[]? operation, bool? enabled, IDynamicEndpointTransfer transfer, CancellationToken ct) =>
                ScaffoldAsync(http, entity, dryRun, routePrefix, group, operation, enabled, transfer, ct))
            .WithSummary("Generates CRUD endpoints for an exposed entity from its EF Core model (?entity=products&routePrefix=/catalog/products&operation=list&operation=get, ?dryRun=true). Disabled unless ?enabled=true; existing routes are skipped; 422 when any endpoint is invalid – nothing is written then.")
            .Produces<DynamicCrudScaffoldResult>()
            .Produces<DynamicCrudScaffoldResult>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    public IEnumerable<string> Features(HttpContext context) =>
        catalog.AvailableTo(AdminTenant(context)).Any() ? [DynamicCrud.Feature] : [];

    private async Task<IResult> ScaffoldAsync(HttpContext http, string? entity, bool? dryRun, string? routePrefix, string? group,
        string[]? operation, bool? enabled, IDynamicEndpointTransfer transfer, CancellationToken ct)
    {
        var tenant = AdminTenant(http);
        if (string.IsNullOrWhiteSpace(entity) || catalog.Find(entity) is not { } found || !found.IsAvailableTo(tenant))
        {
            return TypedResults.Problem($"Unknown entity '{entity}'. Available: {string.Join(", ", catalog.AvailableTo(tenant).Select(e => e.Name))}.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var operations = CrudOperations.None;
        foreach (var name in (operation ?? []).SelectMany(o => o.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!Enum.TryParse<CrudOperation>(name, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
            {
                return TypedResults.Problem($"Unknown operation '{name}'. Use list, get, create, update, patch or delete.", statusCode: StatusCodes.Status400BadRequest);
            }

            operations |= DynamicCrudEntity.Flag(parsed);
        }

        var result = await new DynamicCrudScaffolder(catalog, transfer).ScaffoldAsync(found.Name, new DynamicCrudScaffoldOptions
        {
            DryRun = dryRun ?? false,
            RoutePrefix = string.IsNullOrWhiteSpace(routePrefix) ? null : routePrefix,
            Group = string.IsNullOrWhiteSpace(group) ? null : group,
            Operations = operations == CrudOperations.None ? CrudOperations.All : operations,
            Enabled = enabled ?? false,
            Tenant = tenant,
        }, ct);
        return TypedResults.Json(result, statusCode: result.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
    }

    private static string? AdminTenant(HttpContext http) => DynamicEndpointsEndpointRouteBuilderExtensions.AdminTenant(http);
}
