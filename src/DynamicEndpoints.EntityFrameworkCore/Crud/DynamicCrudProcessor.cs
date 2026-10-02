using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// List, get, create, update, patch and delete of the entities the application exposed with <c>AddEntityFrameworkCrud</c>. The
/// configuration is checked against the allowlist when a definition is saved, and again before every request.
/// </summary>
[DynamicProcessor(DynamicCrud.ProcessorName,
    Description = "CRUD on an entity the application exposed through Entity Framework Core: list (paging, sorting, filters), get, create, update, patch, delete – with ETags.",
    ConfigurationExample = """{ "entity": "products", "operation": "list", "pageSize": 50, "maxPageSize": 200, "sort": "name" }""")]
internal sealed class DynamicCrudProcessor<TContext>(DynamicCrudCatalog catalog) : DynamicEndpointProcessor<DynamicCrudConfig>
    where TContext : DbContext
{
    protected override IEnumerable<string> Validate(DynamicCrudConfig config) => Validate(config, new DynamicEndpointDefinition { Method = DynamicCrudEntity.MethodOf(config.Operation) });

    protected override IEnumerable<string> Validate(DynamicCrudConfig config, DynamicEndpointDefinition definition)
    {
        var tenant = definition.Tenant;
        var entity = catalog.Find(config.Entity);
        if (entity is null || entity.ContextType != typeof(TContext) || !entity.IsAvailableTo(tenant))
        {
            // A tenant learns only about the entities it may use.
            var available = catalog.AvailableTo(tenant).Where(e => e.ContextType == typeof(TContext)).Select(e => e.Name).ToList();
            var list = available.Count == 0 ? "none" : string.Join(", ", available);
            yield return string.IsNullOrWhiteSpace(config.Entity) ? $"'entity' is required. Available: {list}."
                : tenant is not null && entity is not null && entity.ContextType == typeof(TContext)
                    ? $"Entity '{config.Entity}' isn't available to tenant '{tenant}'. Available: {list}."
                    : $"Unknown entity '{config.Entity}'. Available: {list}.";
            yield break;
        }

        foreach (var error in DynamicCrudValidation.Check(entity, config, definition))
        {
            yield return error;
        }
    }

    protected override async Task<IResult> ProcessAsync(DynamicRequest request, DynamicCrudConfig config)
    {
        var entity = catalog.Find(config.Entity);
        if (entity is null || entity.ContextType != typeof(TContext))
        {
            return DynamicCrudResults.Misconfigured(request, $"Entity '{config.Entity}' is not exposed.");
        }

        // The entity may have been taken away from the endpoint's tenant, or the operation from the entity, after it was saved.
        if (!entity.IsAvailableTo(request.Endpoint.Tenant))
        {
            return DynamicCrudResults.Misconfigured(request, $"Entity '{entity.Name}' isn't available to tenant '{request.Endpoint.Tenant}'.");
        }

        if (!entity.Allows(config.Operation) || !string.Equals(request.Endpoint.Method, DynamicCrudEntity.MethodOf(config.Operation), StringComparison.OrdinalIgnoreCase))
        {
            return DynamicCrudResults.Misconfigured(request, $"Operation '{config.Operation}' is not allowed for '{entity.Name}'.");
        }

        // Rows of an entity with a tenant column belong to the endpoint's tenant – or, for shared endpoints, to the request's.
        if (entity.Tenant is not null && request.Tenant is null)
        {
            return DynamicCrudResults.NoTenant(request);
        }

        var db = request.Services.GetRequiredService<TContext>();
        return await entity.ExecuteAsync(new DynamicCrudExecution(request, config, db, entity.Tenant is null ? null : request.Tenant));
    }
}

/// <summary>Checks of an <c>ef-crud</c> configuration against the allowlist and the definition it belongs to.</summary>
internal static class DynamicCrudValidation
{
    public static IEnumerable<string> Check(DynamicCrudEntity entity, DynamicCrudConfig config, DynamicEndpointDefinition definition)
    {
        var operation = config.Operation;
        if (!entity.Allows(operation))
        {
            yield return $"Operation '{Name(operation)}' is not allowed for '{entity.Name}'. Allowed: {string.Join(", ", Enum.GetValues<CrudOperation>().Where(entity.Allows).Select(Name))}.";
            yield break;
        }

        var method = DynamicCrudEntity.MethodOf(operation);
        if (!string.Equals(definition.Method, method, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"Operation '{Name(operation)}' needs a {method} endpoint.";
        }

        var parameters = definition.Parameters;
        if (operation is CrudOperation.Get or CrudOperation.Update or CrudOperation.Patch or CrudOperation.Delete)
        {
            var key = config.Key ?? entity.KeyField!.Name;
            if (!parameters.Any(p => p.Source == ParameterSource.Route && p.Name == key))
            {
                yield return $"The key comes from the route: add the route parameter '{key}' (or set 'key').";
            }
        }
        else if (config.Key is not null)
        {
            yield return "'key' only applies to get, update, patch and delete.";
        }

        var writes = operation is CrudOperation.Create or CrudOperation.Update or CrudOperation.Patch;
        foreach (var p in parameters.Where(p => p.Source is ParameterSource.Body or ParameterSource.Form))
        {
            if (!writes)
            {
                yield return $"Parameter '{p.Name}': only create, update and patch read a body.";
                continue;
            }

            if (p.Source == ParameterSource.Form)
            {
                yield return $"Parameter '{p.Name}': ef-crud reads JSON bodies – use the source Body.";
                continue;
            }

            var field = entity.FindField(p.Name);
            if (field is null)
            {
                yield return $"Parameter '{p.Name}' is not a field of '{entity.Name}'. Writable: {Writable(entity, operation)}.";
            }
            else if (!(operation == CrudOperation.Create ? field.Creatable : field.Writable))
            {
                yield return $"Field '{field.Name}' of '{entity.Name}' is read-only{(field.Creatable ? " after create" : "")}.";
            }
        }

        if (operation != CrudOperation.List)
        {
            if (config.Filters is { Count: > 0 } || config.Sort is not null || config.SortParameter is not null)
            {
                yield return "'filters', 'sort' and 'sortParameter' only apply to list.";
            }

            yield break;
        }

        if (config.PageSize > config.MaxPageSize)
        {
            yield return "'pageSize' can't be larger than 'maxPageSize'.";
        }

        entity.ParseSort(config.Sort, out var sortError);
        if (sortError is not null)
        {
            yield return $"'sort': {sortError}";
        }

        if (config.SortParameter is { } sortParameter && !IsRequestValue(parameters, sortParameter))
        {
            yield return $"'sortParameter': '{sortParameter}' is not a query, header or route parameter of the endpoint.";
        }

        for (var i = 0; i < (config.Filters?.Count ?? 0); i++)
        {
            var filter = config.Filters![i];
            var key = $"'filters[{i}]'";
            if (entity.FindField(filter.Field) is not { Filterable: true } field)
            {
                var filterable = entity.Fields.Where(f => f.Filterable).Select(f => f.Name).ToList();
                yield return $"{key}: '{filter.Field}' is not a filterable field. Filterable: {(filterable.Count == 0 ? "none" : string.Join(", ", filterable))}.";
                continue;
            }

            if (!field.Operators.Contains(filter.Operator))
            {
                yield return $"{key}: '{Name(filter.Operator)}' can't be used on '{field.Name}'. Allowed: {string.Join(", ", field.Operators.Select(Name))}.";
            }

            if ((filter.Parameter is null) == (filter.Value is null))
            {
                yield return $"{key}: set either 'parameter' or 'value'.";
            }
            else if (filter.Parameter is { } parameter && !IsRequestValue(parameters, parameter))
            {
                yield return $"{key}: '{parameter}' is not a query, header or route parameter of the endpoint.";
            }
            else if (filter.Value is { } value && !IsValid(field, filter.Operator, value))
            {
                yield return $"{key}: the value doesn't fit field '{field.Name}'.";
            }
        }
    }

    public static string Name(CrudOperation operation) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(operation.ToString());

    public static string Name(CrudFilterOperator op) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(op.ToString());

    private static string Writable(DynamicCrudEntity entity, CrudOperation operation)
    {
        var writable = entity.Fields.Where(f => operation == CrudOperation.Create ? f.Creatable : f.Writable).Select(f => f.Name).ToList();
        return writable.Count == 0 ? "none" : string.Join(", ", writable);
    }

    private static bool IsRequestValue(IReadOnlyList<ParameterDefinition> parameters, string name) =>
        parameters.Any(p => p.Name == name && p.Source is ParameterSource.Query or ParameterSource.Header or ParameterSource.Route);

    private static bool IsValid(DynamicCrudField field, CrudFilterOperator op, JsonNode value) => op == CrudFilterOperator.In
        ? value is JsonArray { Count: <= 100 } array && array.All(v => field.TryFromJson(v, out _))
        : field.TryFromJson(value, out _);
}

/// <summary>Constants of the <c>ef-crud</c> processor.</summary>
public static class DynamicCrud
{
    /// <summary>Default name of the processor: <c>ef-crud</c>.</summary>
    public const string ProcessorName = "ef-crud";

    /// <summary>Name of the admin API feature in <see cref="DynamicEndpointsAdminInfo.Features"/>: <c>crud</c>.</summary>
    public const string Feature = "crud";
}
