using System.Text.Json.Nodes;

namespace DynamicEndpoints.EntityFrameworkCore;

public sealed record DynamicCrudScaffoldOptions
{
    /// <summary>Route of the collection, e.g. <c>/catalog/products</c>; items get <c>/{key}</c>. Default: <c>/</c> + the entity name.</summary>
    public string? RoutePrefix { get; init; }

    /// <summary>Group of the endpoints. Default: the entity name.</summary>
    public string? Group { get; init; }

    /// <summary>Which endpoints to generate – only those the entity allows are. Default: all it allows.</summary>
    public CrudOperations Operations { get; init; } = CrudOperations.All;

    /// <summary>
    /// Generated endpoints are disabled by default, so they go live only after somebody reviewed them (security, rate limits).
    /// Disabled endpoints don't take part in route conflict checks either.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Tenant of the endpoints. A tenant's admin API always uses its own tenant.</summary>
    public string? Tenant { get; init; }

    /// <summary>Reports what would be created, and writes nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>What happened (or would happen) with one generated endpoint.</summary>
public sealed record DynamicCrudScaffoldedOperation
{
    public CrudOperation Operation { get; init; }

    public string Method { get; init; } = "";

    public string Route { get; init; } = "";

    /// <summary><see cref="DynamicEndpointImportAction.Create"/>, <see cref="DynamicEndpointImportAction.Skip"/> (the route exists) or <see cref="DynamicEndpointImportAction.Invalid"/>.</summary>
    public DynamicEndpointImportAction Action { get; init; }

    public DynamicEndpointDefinition Definition { get; init; } = new();

    /// <summary>Validation errors of the definition.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

/// <summary>The result of <see cref="IDynamicCrudScaffolder.ScaffoldAsync"/> – the same shape as the OpenAPI import's.</summary>
public sealed record DynamicCrudScaffoldResult
{
    /// <summary>Every endpoint was (or would be) created or already exists. Nothing is written when one is invalid.</summary>
    public bool Succeeded { get; init; }

    public bool DryRun { get; init; }

    public string Entity { get; init; } = "";

    public IReadOnlyList<DynamicCrudScaffoldedOperation> Operations { get; init; } = [];

    public int Created => Operations.Count(o => o.Action == DynamicEndpointImportAction.Create);

    public int Skipped => Operations.Count(o => o.Action == DynamicEndpointImportAction.Skip);

    public int Invalid => Operations.Count(o => o.Action == DynamicEndpointImportAction.Invalid);
}

/// <summary>An exposed entity as admins see it – <c>GET /crud/entities</c> of the admin API.</summary>
public sealed record DynamicCrudEntityInfo
{
    public string Name { get; init; } = "";

    /// <summary>Processor of the entity's endpoints, normally <c>ef-crud</c>.</summary>
    public string Processor { get; init; } = DynamicCrud.ProcessorName;

    /// <summary>Allowed operations: <c>list</c>, <c>get</c>, <c>create</c>, <c>update</c>, <c>patch</c>, <c>delete</c>.</summary>
    public IReadOnlyList<string> Operations { get; init; } = [];

    /// <summary>Name of the key field (route parameter), <c>null</c> without a single-property key.</summary>
    public string? Key { get; init; }

    /// <summary>Rows are filtered by tenant.</summary>
    public bool TenantColumn { get; init; }

    /// <summary>ETags and <c>If-Match</c> are supported.</summary>
    public bool ConcurrencyToken { get; init; }

    public IReadOnlyList<DynamicCrudFieldInfo> Fields { get; init; } = [];
}

/// <summary>A field of <see cref="DynamicCrudEntityInfo"/>.</summary>
public sealed record DynamicCrudFieldInfo
{
    public string Name { get; init; } = "";

    /// <summary>JSON Schema of the value (type, format, maxLength, enum, …).</summary>
    public JsonObject Schema { get; init; } = new();

    /// <summary>Never written by update or patch (keys may still be set on create, see <see cref="Creatable"/>).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Can be set on create.</summary>
    public bool Creatable { get; init; }

    public bool Required { get; init; }

    public bool Key { get; init; }

    public bool Filterable { get; init; }

    public bool Sortable { get; init; }

    /// <summary>Filter operators of the field (when filterable).</summary>
    public IReadOnlyList<string> Operators { get; init; } = [];
}

/// <summary>
/// Generates endpoint definitions for an exposed entity from its EF metadata: list, get, create, update, patch and delete as allowed,
/// with route key, parameters and constraints (types, required, max length, precision, enum values). Works like the OpenAPI import:
/// existing routes are skipped, nothing is written when an endpoint is invalid, and generated endpoints are disabled by default.
/// </summary>
public interface IDynamicCrudScaffolder
{
    /// <summary>The entities the endpoints of <paramref name="tenant"/> may use (<c>null</c>: shared endpoints).</summary>
    IReadOnlyList<DynamicCrudEntityInfo> Entities(string? tenant = null);

    /// <summary>The definitions, without validating or saving them.</summary>
    /// <exception cref="ArgumentException">The entity isn't exposed (or not to the tenant).</exception>
    IReadOnlyList<DynamicEndpointDefinition> Scaffold(string entity, DynamicCrudScaffoldOptions? options = null);

    /// <summary>Generates, validates and – unless <see cref="DynamicCrudScaffoldOptions.DryRun"/> – creates the endpoints.</summary>
    /// <exception cref="ArgumentException">The entity isn't exposed (or not to the tenant).</exception>
    Task<DynamicCrudScaffoldResult> ScaffoldAsync(string entity, DynamicCrudScaffoldOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class DynamicCrudScaffolder(DynamicCrudCatalog catalog, IDynamicEndpointTransfer transfer) : IDynamicCrudScaffolder
{
    public IReadOnlyList<DynamicCrudEntityInfo> Entities(string? tenant = null) => catalog.AvailableTo(tenant).Select(Describe).ToList();

    public IReadOnlyList<DynamicEndpointDefinition> Scaffold(string entity, DynamicCrudScaffoldOptions? options = null)
    {
        options ??= new DynamicCrudScaffoldOptions();
        var found = catalog.Find(entity);
        if (found is null || !found.IsAvailableTo(options.Tenant))
        {
            throw new ArgumentException($"Entity '{entity}' is not exposed{(options.Tenant is null ? "" : $" to tenant '{options.Tenant}'")}.", nameof(entity));
        }

        return Enum.GetValues<CrudOperation>()
            .Where(o => found.Allows(o) && options.Operations.HasFlag(DynamicCrudEntity.Flag(o)))
            .Select(o => Definition(found, o, options))
            .ToList();
    }

    public async Task<DynamicCrudScaffoldResult> ScaffoldAsync(string entity, DynamicCrudScaffoldOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new DynamicCrudScaffoldOptions();
        var definitions = Scaffold(entity, options);

        // Without ids the transfer matches existing endpoints by method and route – those are skipped. A tenant-scoped transfer
        // (the tenant admin API) assigns its tenant and sees only its own endpoints.
        var rehearsal = await RunAsync(definitions, dryRun: true, cancellationToken);
        var operations = Map(definitions, rehearsal);
        var succeeded = operations.All(o => o.Action != DynamicEndpointImportAction.Invalid);
        if (!options.DryRun && succeeded && operations.Any(o => o.Action == DynamicEndpointImportAction.Create))
        {
            var created = await RunAsync(definitions, dryRun: false, cancellationToken);
            operations = Map(definitions, created);
            succeeded = created.Succeeded;
        }

        return new DynamicCrudScaffoldResult { Succeeded = succeeded, DryRun = options.DryRun, Entity = catalog.Find(entity)!.Name, Operations = operations };
    }

    internal static DynamicCrudEntityInfo Describe(DynamicCrudEntity entity) => new()
    {
        Name = entity.Name,
        Processor = entity.ProcessorName,
        Operations = Enum.GetValues<CrudOperation>().Where(entity.Allows).Select(DynamicCrudValidation.Name).ToList(),
        Key = entity.KeyField?.Name,
        TenantColumn = entity.Tenant is not null,
        ConcurrencyToken = entity.ConcurrencyTokens.Count > 0,
        Fields = entity.Fields.Select(f => new DynamicCrudFieldInfo
        {
            Name = f.Name,
            Schema = f.Schema(forResponse: false),
            ReadOnly = !f.Writable,
            Creatable = f.Creatable,
            Required = f.Required,
            Key = f.IsKey,
            Filterable = f.Filterable,
            Sortable = f.Sortable,
            Operators = f.Filterable ? f.Operators.Select(DynamicCrudValidation.Name).ToList() : [],
        }).ToList(),
    };

    private Task<DynamicEndpointImportResult> RunAsync(IEnumerable<DynamicEndpointDefinition> definitions, bool dryRun, CancellationToken cancellationToken) =>
        transfer.ImportAsync(
            new DynamicEndpointExport { Endpoints = definitions.ToList() },
            new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Create, DryRun = dryRun },
            cancellationToken);

    private static List<DynamicCrudScaffoldedOperation> Map(IReadOnlyList<DynamicEndpointDefinition> definitions, DynamicEndpointImportResult result) =>
        definitions.Select((d, i) =>
        {
            var item = i < result.Items.Count ? result.Items[i] : null;
            return new DynamicCrudScaffoldedOperation
            {
                Operation = d.ProcessorConfig!["operation"]!.GetValue<string>() is var name ? Enum.Parse<CrudOperation>(name, ignoreCase: true) : default,
                Method = d.Method,
                Route = d.Route,
                Action = item?.Action ?? DynamicEndpointImportAction.Invalid,
                Definition = result.DryRun || item is null ? d : d with { Id = item.Id },
                Errors = item?.Errors ?? new Dictionary<string, string[]>(),
            };
        }).ToList();

    private static DynamicEndpointDefinition Definition(DynamicCrudEntity entity, CrudOperation operation, DynamicCrudScaffoldOptions options)
    {
        var collection = "/" + (options.RoutePrefix ?? entity.Name).Trim().Trim('/');
        var key = entity.KeyField;
        var item = key is null ? collection : $"{collection}/{{{key.Name}}}";
        var config = new DynamicCrudConfig { Entity = entity.Name, Operation = operation };
        var parameters = new List<ParameterDefinition>();
        var (route, name) = operation switch
        {
            CrudOperation.List => (collection, $"{entity.Name}: list"),
            CrudOperation.Get => (item, $"{entity.Name}: get"),
            CrudOperation.Create => (collection, $"{entity.Name}: create"),
            CrudOperation.Update => (item, $"{entity.Name}: replace"),
            CrudOperation.Patch => (item, $"{entity.Name}: update"),
            _ => (item, $"{entity.Name}: delete"),
        };

        if (operation is CrudOperation.Get or CrudOperation.Update or CrudOperation.Patch or CrudOperation.Delete)
        {
            parameters.Add(Parameter(key!, ParameterSource.Route, required: true) with { Description = $"Key of the {entity.Name} entity." });
        }

        switch (operation)
        {
            case CrudOperation.List:
                parameters.Add(new ParameterDefinition { Name = "page", Source = ParameterSource.Query, Type = ParameterType.Integer, Minimum = 1, Default = 1, Description = "Page number, from 1." });
                parameters.Add(new ParameterDefinition
                {
                    Name = "pageSize", Source = ParameterSource.Query, Type = ParameterType.Integer, Minimum = 1, Maximum = config.MaxPageSize,
                    Default = config.PageSize, Description = "Items per page.",
                });
                var sortable = entity.Fields.Where(f => f.Sortable).Select(f => f.Name).ToList();
                if (sortable.Count > 0)
                {
                    config.SortParameter = "sort";
                    parameters.Add(new ParameterDefinition
                    {
                        Name = "sort", Source = ParameterSource.Query, Type = ParameterType.String, MaxLength = 200,
                        Pattern = "^-?[A-Za-z0-9_]+(,-?[A-Za-z0-9_]+)*$",
                        Description = $"Order: fields separated by commas, '-' for descending. Sortable: {string.Join(", ", sortable)}.",
                    });
                }

                config.Filters = [];
                foreach (var field in entity.Fields.Where(f => f.Filterable))
                {
                    AddFilter(config, parameters, field, field.Name, CrudFilterOperator.Eq, $"Only items whose {field.Name} equals this value.");
                    if (field.Operators.Contains(CrudFilterOperator.Gte))
                    {
                        var suffix = char.ToUpperInvariant(field.Name[0]) + field.Name[1..];
                        AddFilter(config, parameters, field, "min" + suffix, CrudFilterOperator.Gte, $"Only items whose {field.Name} is at least this value.");
                        AddFilter(config, parameters, field, "max" + suffix, CrudFilterOperator.Lte, $"Only items whose {field.Name} is at most this value.");
                    }
                }

                if (config.Filters.Count == 0)
                {
                    config.Filters = null;
                }

                break;

            case CrudOperation.Create or CrudOperation.Update or CrudOperation.Patch:
                foreach (var field in entity.Fields.Where(f => operation == CrudOperation.Create ? f.Creatable : f.Writable))
                {
                    var required = operation != CrudOperation.Patch && (field.Required || (operation == CrudOperation.Create && field.IsKey));
                    parameters.Add(Parameter(field, ParameterSource.Body, required));
                }

                break;
        }

        return new DynamicEndpointDefinition
        {
            Method = DynamicCrudEntity.MethodOf(operation),
            Route = route,
            Name = name,
            Description = $"Generated from the EF Core model of '{entity.Name}' (ef-crud).",
            Group = options.Group ?? entity.Name,
            Tenant = options.Tenant,
            Processor = entity.ProcessorName,
            ProcessorConfig = DynamicEndpoint.ToObject(config),
            Parameters = parameters,
            Enabled = options.Enabled,
            Origin = new DynamicEndpointOrigin { Kind = DynamicCrud.OriginKind, Document = entity.Name, Operation = DynamicCrudValidation.Name(operation) },
        };
    }

    private static void AddFilter(DynamicCrudConfig config, List<ParameterDefinition> parameters, DynamicCrudField field, string name, CrudFilterOperator op, string description)
    {
        if (parameters.Any(p => p.Name == name))
        {
            return;
        }

        parameters.Add(Parameter(field, ParameterSource.Query, required: false) with { Name = name, Description = description });
        config.Filters!.Add(new DynamicCrudFilter { Field = field.Name, Operator = op, Parameter = name });
    }

    /// <summary>A parameter with the type and constraints of a field.</summary>
    internal static ParameterDefinition Parameter(DynamicCrudField field, ParameterSource source, bool required)
    {
        var parameter = new ParameterDefinition { Name = field.Name, Source = source, Required = required };
        switch (field.Kind)
        {
            case DynamicCrudValueKind.Integer:
                parameter = parameter with { Type = ParameterType.Integer };
                if (DynamicCrudValues.IntegerRange(field.ValueType) is var (min, max))
                {
                    parameter = parameter with { Minimum = min, Maximum = max };
                }

                break;
            case DynamicCrudValueKind.Number:
                var limit = DynamicCrudValues.DecimalLimit(field.Property);
                parameter = parameter with { Type = ParameterType.Number, Minimum = -limit, Maximum = limit };
                break;
            case DynamicCrudValueKind.Boolean:
                parameter = parameter with { Type = ParameterType.Boolean };
                break;
            case DynamicCrudValueKind.Guid:
                parameter = parameter with { Type = ParameterType.Guid };
                break;
            case DynamicCrudValueKind.DateTime:
                parameter = parameter with { Type = ParameterType.DateTime };
                break;
            case DynamicCrudValueKind.Date:
                parameter = parameter with { Type = ParameterType.Date };
                break;
            case DynamicCrudValueKind.Time:
                parameter = parameter with { Type = ParameterType.String, Format = ParameterFormat.Time };
                break;
            case DynamicCrudValueKind.Enum:
                parameter = parameter with { Type = ParameterType.String, AllowedValues = field.EnumValues!.Select(v => (JsonNode?)JsonValue.Create(v)).ToList() };
                break;
            default:
                parameter = parameter with { Type = ParameterType.String, MaxLength = field.MaxLength };
                break;
        }

        return parameter;
    }
}
