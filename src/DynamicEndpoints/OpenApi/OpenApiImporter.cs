using System.Text.Json.Nodes;
using DynamicEndpoints.OpenApi;

namespace DynamicEndpoints;

public sealed record OpenApiImportOptions
{
    /// <summary>Processor of the imported endpoints. Without one, <see cref="DynamicEndpointsOptions.DefaultProcessor"/> applies.</summary>
    public string? Processor { get; init; }

    public JsonObject? ProcessorConfig { get; init; }

    /// <summary>Prepended to every path, e.g. <c>/partners/v1</c>.</summary>
    public string? RoutePrefix { get; init; }

    /// <summary>Group of all imported endpoints. Default: the first tag of each operation.</summary>
    public string? Group { get; init; }

    /// <summary>Only operations with one of these tags. Default: all.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Imported endpoints are disabled by default: they are skeletons until somebody reviewed them and chose a processor. Disabled
    /// endpoints don't take part in route conflict checks either.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Reports what would be created and what couldn't be mapped, and writes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Creates the valid endpoints even when others are invalid. By default nothing is created when any endpoint is invalid.</summary>
    public bool SkipInvalid { get; init; }
}

/// <summary>What happened (or would happen) with one operation of the document.</summary>
public sealed record OpenApiImportedOperation
{
    public string Method { get; init; } = "";

    /// <summary>Path in the OpenAPI document.</summary>
    public string Path { get; init; } = "";

    public string? OperationId { get; init; }

    /// <summary>
    /// <see cref="DynamicEndpointImportAction.Create"/>, <see cref="DynamicEndpointImportAction.Skip"/> (the route exists, or the
    /// operation can't be mapped – e.g. HEAD) or <see cref="DynamicEndpointImportAction.Invalid"/>.
    /// </summary>
    public DynamicEndpointImportAction Action { get; init; }

    /// <summary>The definition (skeleton) the operation was mapped to; <c>null</c> when the operation can't be mapped at all.</summary>
    public DynamicEndpointDefinition? Definition { get; init; }

    /// <summary>Parts of the operation without a counterpart in the definition model – check them by hand.</summary>
    public IReadOnlyList<string> Unmapped { get; init; } = [];

    /// <summary>Validation errors of the mapped definition.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

public sealed record OpenApiImportResult
{
    /// <summary>All mapped operations were (or would be) created or already exist.</summary>
    public bool Succeeded { get; init; }

    public bool DryRun { get; init; }

    public IReadOnlyList<OpenApiImportedOperation> Operations { get; init; } = [];

    /// <summary>Document-level remarks.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Created => Operations.Count(o => o.Action == DynamicEndpointImportAction.Create);

    public int Skipped => Operations.Count(o => o.Action == DynamicEndpointImportAction.Skip);

    public int Invalid => Operations.Count(o => o.Action == DynamicEndpointImportAction.Invalid);
}

/// <summary>
/// Turns an OpenAPI 3.x document into endpoint skeletons: routes, methods, path/query/header parameters with types and constraints,
/// JSON and form body properties, response schemas and examples. Operations whose route already exists are skipped.
/// </summary>
public interface IDynamicEndpointOpenApiImporter
{
    /// <summary>Maps the document without validating or saving anything.</summary>
    /// <exception cref="FormatException">Not an OpenAPI 3.x document.</exception>
    OpenApiImportResult Convert(JsonNode document, OpenApiImportOptions? options = null);

    /// <summary>Maps, validates and – unless <see cref="OpenApiImportOptions.DryRun"/> – creates the endpoints.</summary>
    /// <exception cref="FormatException">Not an OpenAPI 3.x document.</exception>
    Task<OpenApiImportResult> ImportAsync(JsonNode document, OpenApiImportOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class DynamicEndpointOpenApiImporter(IDynamicEndpointTransfer transfer) : IDynamicEndpointOpenApiImporter
{
    internal DynamicEndpointOpenApiImporter ForTenant(string? tenant) =>
        transfer is DynamicEndpointTransfer t ? new(t.ForTenant(tenant)) : throw DynamicEndpointTenantExtensions.NotTenantAware(transfer);

    public OpenApiImportResult Convert(JsonNode document, OpenApiImportOptions? options = null)
    {
        var (operations, warnings) = OpenApiConverter.Convert(document, options ?? new OpenApiImportOptions());
        var mapped = operations.Select(o => new OpenApiImportedOperation
        {
            Method = o.Method,
            Path = o.Path,
            OperationId = o.OperationId,
            Action = o.Definition is null ? DynamicEndpointImportAction.Skip : DynamicEndpointImportAction.Create,
            Definition = o.Definition,
            Unmapped = o.Unmapped,
        }).ToList();
        return new OpenApiImportResult { Succeeded = true, DryRun = true, Operations = mapped, Warnings = warnings };
    }

    public async Task<OpenApiImportResult> ImportAsync(JsonNode document, OpenApiImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenApiImportOptions();
        var converted = Convert(document, options);
        var operations = converted.Operations.ToList();

        // Without ids, the transfer matches existing endpoints by method and route – those are skipped. Its items come back in
        // the order of the definitions, which maps them to the operations.
        var candidates = operations
            .Select((o, i) => (Index: i, o.Definition))
            .Where(c => c.Definition is not null)
            .ToList();

        var rehearsal = await RunAsync(candidates.Select(c => c.Definition!), dryRun: true, cancellationToken);
        Apply(operations, candidates, rehearsal);

        var valid = candidates.Where(c => operations[c.Index].Action == DynamicEndpointImportAction.Create).ToList();
        var succeeded = operations.All(o => o.Action != DynamicEndpointImportAction.Invalid);
        if (!options.DryRun && valid.Count > 0 && (succeeded || options.SkipInvalid))
        {
            var created = await RunAsync(valid.Select(c => c.Definition!), dryRun: false, cancellationToken);
            Apply(operations, valid, created);
            succeeded = operations.All(o => o.Action != DynamicEndpointImportAction.Invalid);
        }

        return converted with { Succeeded = succeeded, DryRun = options.DryRun, Operations = operations };
    }

    private Task<DynamicEndpointImportResult> RunAsync(IEnumerable<DynamicEndpointDefinition> definitions, bool dryRun, CancellationToken cancellationToken) =>
        transfer.ImportAsync(
            new DynamicEndpointExport { Endpoints = definitions.ToList() },
            new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Create, DryRun = dryRun },
            cancellationToken);

    private static void Apply(List<OpenApiImportedOperation> operations, List<(int Index, DynamicEndpointDefinition? Definition)> candidates, DynamicEndpointImportResult result)
    {
        for (var i = 0; i < candidates.Count && i < result.Items.Count; i++)
        {
            var (index, definition) = candidates[i];
            var item = result.Items[i];
            operations[index] = operations[index] with
            {
                Action = item.Action,
                Errors = item.Errors,
                Definition = result.DryRun ? definition : definition! with { Id = item.Id },
            };
        }
    }
}
