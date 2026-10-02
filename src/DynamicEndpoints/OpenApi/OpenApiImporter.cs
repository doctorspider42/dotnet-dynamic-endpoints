using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DynamicEndpoints.OpenApi;
using DynamicEndpoints.Processing;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints;

public sealed record OpenApiImportOptions
{
    /// <summary>
    /// Processor of the imported endpoints – unless the document (<c>x-dynamic-endpoints-processor</c>) or
    /// <see cref="ProcessorsByTag"/> chooses one. Without any, <see cref="DynamicEndpointsOptions.DefaultProcessor"/> applies.
    /// </summary>
    public string? Processor { get; init; }

    /// <summary>Configuration of <see cref="Processor"/>.</summary>
    public JsonObject? ProcessorConfig { get; init; }

    /// <summary>
    /// Processor (and configuration) per tag: an operation gets the mapping of its first tag that has one. Vendor extensions on the
    /// operation or its path win over it, it wins over the document-level extension and <see cref="Processor"/>. Tags match case-insensitively.
    /// </summary>
    public IReadOnlyDictionary<string, OpenApiProcessorMapping>? ProcessorsByTag { get; init; }

    /// <summary>
    /// Mock mode: every operation gets the built-in <c>response</c> processor answering with the status code and example (or a body
    /// generated from the schema) of its first 2xx response. <c>x-dynamic-endpoints-processor</c> extensions still win;
    /// <see cref="ProcessorsByTag"/> and <see cref="Processor"/> are ignored. Needs <c>AddResponseTemplateProcessor()</c>.
    /// </summary>
    public bool Mock { get; init; }

    /// <summary>
    /// <see cref="DynamicEndpointImportMode.Create"/> (default) skips operations whose endpoint exists,
    /// <see cref="DynamicEndpointImportMode.Upsert"/> also updates endpoints imported from the same operation before, and
    /// <see cref="DynamicEndpointImportMode.Sync"/> also deletes endpoints imported from this document whose operation is gone.
    /// Endpoints that weren't imported from the document are never changed.
    /// </summary>
    public DynamicEndpointImportMode Mode { get; init; } = DynamicEndpointImportMode.Create;

    /// <summary>
    /// Identity of the document in the imported endpoints' <see cref="DynamicEndpointDefinition.Origin"/> – what a re-import matches.
    /// Default: the document's <c>info.title</c>. Set it when the title changes, or to import one document twice (e.g. under two prefixes).
    /// </summary>
    public string? DocumentId { get; init; }

    /// <summary>Prepended to every path, e.g. <c>/partners/v1</c>.</summary>
    public string? RoutePrefix { get; init; }

    /// <summary>Group of all imported endpoints. Default: the first tag of each operation.</summary>
    public string? Group { get; init; }

    /// <summary>Only operations with one of these tags. Default: all.</summary>
    public IReadOnlyList<string>? Tags { get; init; }

    /// <summary>
    /// Imported endpoints are disabled by default: they are skeletons until somebody reviewed them and chose a processor. Disabled
    /// endpoints don't take part in route conflict checks either. Updates keep the enabled state of the existing endpoint.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Reports what would be created, updated or deleted and what couldn't be mapped, and writes nothing.</summary>
    public bool DryRun { get; init; }

    /// <summary>Imports the valid operations even when others are invalid. By default nothing is written when any operation is invalid.</summary>
    public bool SkipInvalid { get; init; }
}

/// <summary>Processor and configuration for the operations of a tag (<see cref="OpenApiImportOptions.ProcessorsByTag"/>).</summary>
public sealed record OpenApiProcessorMapping
{
    public string Processor { get; init; } = "";

    public JsonObject? ProcessorConfig { get; init; }
}

/// <summary>Where the processor of an imported operation came from – in order of precedence.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OpenApiProcessorSource>))]
public enum OpenApiProcessorSource
{
    /// <summary><c>x-dynamic-endpoints-processor</c> on the operation.</summary>
    OperationExtension,
    /// <summary><c>x-dynamic-endpoints-processor</c> on the path item.</summary>
    PathExtension,
    /// <summary><see cref="OpenApiImportOptions.ProcessorsByTag"/>.</summary>
    Tag,
    /// <summary><c>x-dynamic-endpoints-processor</c> at the root of the document.</summary>
    DocumentExtension,
    /// <summary><see cref="OpenApiImportOptions.Mock"/>: the <c>response</c> processor with the documented response.</summary>
    Mock,
    /// <summary><see cref="OpenApiImportOptions.Processor"/>.</summary>
    Option,
    /// <summary>None of the above: <see cref="DynamicEndpointsOptions.DefaultProcessor"/>.</summary>
    Default,
    /// <summary>An update kept the processor and configuration of the existing endpoint, because only the default applied.</summary>
    Kept,
}

/// <summary>What happened (or would happen) with one operation of the document.</summary>
public sealed record OpenApiImportedOperation
{
    public string Method { get; init; } = "";

    /// <summary>Path in the OpenAPI document – the route of the endpoint for a <see cref="DynamicEndpointImportAction.Delete"/>.</summary>
    public string Path { get; init; } = "";

    public string? OperationId { get; init; }

    /// <summary>
    /// <see cref="DynamicEndpointImportAction.Create"/>, <see cref="DynamicEndpointImportAction.Update"/> (see <see cref="Changes"/>),
    /// <see cref="DynamicEndpointImportAction.Unchanged"/>, <see cref="DynamicEndpointImportAction.Delete"/> (sync: the operation is
    /// gone from the document), <see cref="DynamicEndpointImportAction.Skip"/> (see <see cref="Reason"/>) or
    /// <see cref="DynamicEndpointImportAction.Invalid"/>.
    /// </summary>
    public DynamicEndpointImportAction Action { get; init; }

    /// <summary>Id of the endpoint that is (or would be) updated or deleted, or that was created.</summary>
    public Guid? Id { get; init; }

    /// <summary>Why the operation is skipped, e.g. its route belongs to an endpoint that wasn't imported from it.</summary>
    public string? Reason { get; init; }

    /// <summary>The processor the endpoint gets; <c>null</c> when <see cref="DynamicEndpointsOptions.DefaultProcessor"/> applies and is not set.</summary>
    public string? Processor { get; init; }

    /// <summary>Where <see cref="Processor"/> came from.</summary>
    public OpenApiProcessorSource ProcessorSource { get; init; }

    /// <summary>Details of <see cref="ProcessorSource"/>, e.g. <c>tag 'Pets'</c> or <c>mock: 200 with the documented example</c>.</summary>
    public string? ProcessorReason { get; init; }

    /// <summary>Changed properties of an update, e.g. <c>parameters</c>, <c>route</c>.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>
    /// The definition the operation was mapped to – for an update, merged with the existing endpoint; for a delete, the existing
    /// endpoint. <c>null</c> when the operation can't be mapped at all.
    /// </summary>
    public DynamicEndpointDefinition? Definition { get; init; }

    /// <summary>Parts of the operation without a counterpart in the definition model – check them by hand.</summary>
    public IReadOnlyList<string> Unmapped { get; init; } = [];

    /// <summary>Validation errors of the mapped definition.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; init; } = new Dictionary<string, string[]>();
}

public sealed record OpenApiImportResult
{
    /// <summary>Every operation was (or would be) imported. When <c>false</c> and not <see cref="OpenApiImportOptions.SkipInvalid"/>, nothing was written.</summary>
    public bool Succeeded { get; init; }

    public bool DryRun { get; init; }

    public DynamicEndpointImportMode Mode { get; init; }

    /// <summary>The document id recorded in <see cref="DynamicEndpointOrigin.Document"/>.</summary>
    public string? Document { get; init; }

    /// <summary>Tags of the document and its operations – e.g. to offer a <see cref="OpenApiImportOptions.ProcessorsByTag"/> mapping.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<OpenApiImportedOperation> Operations { get; init; } = [];

    /// <summary>Document-level remarks.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Created => Count(DynamicEndpointImportAction.Create);

    public int Updated => Count(DynamicEndpointImportAction.Update);

    public int Deleted => Count(DynamicEndpointImportAction.Delete);

    public int Unchanged => Count(DynamicEndpointImportAction.Unchanged);

    public int Skipped => Count(DynamicEndpointImportAction.Skip);

    public int Invalid => Count(DynamicEndpointImportAction.Invalid);

    /// <summary>Something is (or would be) created, updated or deleted.</summary>
    public bool HasChanges => Created + Updated + Deleted > 0;

    private int Count(DynamicEndpointImportAction action) => Operations.Count(o => o.Action == action);
}

/// <summary>
/// Turns an OpenAPI 3.x document into endpoint skeletons: routes, methods, path/query/header parameters with types and constraints,
/// JSON and form body properties, response schemas and examples – with a processor per operation, a mock mode, and re-imports
/// that update (or sync) the endpoints imported from the document before.
/// </summary>
public interface IDynamicEndpointOpenApiImporter
{
    /// <summary>Maps the document without validating, matching or saving anything.</summary>
    /// <exception cref="FormatException">Not an OpenAPI 3.x document.</exception>
    OpenApiImportResult Convert(JsonNode document, OpenApiImportOptions? options = null);

    /// <summary>
    /// Maps, matches against the existing endpoints, validates and – unless <see cref="OpenApiImportOptions.DryRun"/> – writes.
    /// </summary>
    /// <exception cref="FormatException">Not an OpenAPI 3.x document, or a re-import of a document without an id.</exception>
    Task<OpenApiImportResult> ImportAsync(JsonNode document, OpenApiImportOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class DynamicEndpointOpenApiImporter(
    IDynamicEndpointTransfer transfer,
    ProcessorRegistry processors,
    IOptions<DynamicEndpointsOptions> endpointOptions) : IDynamicEndpointOpenApiImporter
{
    internal DynamicEndpointOpenApiImporter ForTenant(string? tenant) =>
        transfer is DynamicEndpointTransfer t ? new(t.ForTenant(tenant), processors, endpointOptions) : throw DynamicEndpointTenantExtensions.NotTenantAware(transfer);

    public OpenApiImportResult Convert(JsonNode document, OpenApiImportOptions? options = null)
    {
        options ??= new OpenApiImportOptions();
        var conversion = OpenApiConverter.Convert(document, options);
        return Result(conversion, options, MapOperations(conversion), succeeded: true, dryRun: true);
    }

    public async Task<OpenApiImportResult> ImportAsync(JsonNode document, OpenApiImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new OpenApiImportOptions();
        var conversion = OpenApiConverter.Convert(document, options);
        if (options.Mode != DynamicEndpointImportMode.Create && conversion.DocumentId is null)
        {
            throw new FormatException($"The document has no 'info.title' – pass a document id to {options.Mode.ToString().ToLowerInvariant()} it.");
        }

        var operations = MapOperations(conversion);
        RequireMockProcessor(operations);

        var current = (await transfer.ExportAsync(cancellationToken: cancellationToken)).Endpoints.ToList();
        var plan = Match(operations, current, conversion, options);

        var rehearsal = await RunAsync(plan, operations, dryRun: true, cancellationToken);
        Apply(operations, plan, rehearsal);

        var succeeded = operations.All(o => o.Action != DynamicEndpointImportAction.Invalid);
        if (!options.DryRun && (succeeded || options.SkipInvalid))
        {
            var valid = plan.Where(p => operations[p.Index].Action is DynamicEndpointImportAction.Create or DynamicEndpointImportAction.Update or DynamicEndpointImportAction.Delete).ToList();
            if (valid.Count > 0)
            {
                var written = await RunAsync(valid, operations, dryRun: false, cancellationToken);
                Apply(operations, valid, written);
                succeeded = operations.All(o => o.Action != DynamicEndpointImportAction.Invalid);
            }
        }

        return Result(conversion, options, operations, succeeded, options.DryRun);
    }

    private List<OpenApiImportedOperation> MapOperations(OpenApiConverter.Conversion conversion) =>
        conversion.Operations.Select(o => new OpenApiImportedOperation
        {
            Method = o.Method,
            Path = o.Path,
            OperationId = o.OperationId,
            Action = o.Definition is null ? DynamicEndpointImportAction.Skip : DynamicEndpointImportAction.Create,
            Reason = o.Definition is null ? "The operation can't be mapped." : null,
            Definition = o.Definition,
            Processor = o.Definition is null ? null : o.Definition.Processor ?? endpointOptions.Value.DefaultProcessor,
            ProcessorSource = o.ProcessorSource,
            ProcessorReason = o.ProcessorReason,
            Unmapped = o.Unmapped,
        }).ToList();

    private static OpenApiImportResult Result(
        OpenApiConverter.Conversion conversion, OpenApiImportOptions options, List<OpenApiImportedOperation> operations, bool succeeded, bool dryRun) => new()
    {
        Succeeded = succeeded,
        DryRun = dryRun,
        Mode = options.Mode,
        Document = conversion.DocumentId,
        Tags = conversion.Tags,
        Operations = operations,
        Warnings = conversion.Warnings,
    };

    // Mocks need the built-in response processor; say how to get it instead of "unknown processor".
    private void RequireMockProcessor(List<OpenApiImportedOperation> operations)
    {
        if (processors.TryGet(OpenApiConverter.MockProcessor, out _))
        {
            return;
        }

        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i] is { ProcessorSource: OpenApiProcessorSource.Mock, Definition: not null })
            {
                operations[i] = operations[i] with
                {
                    Action = DynamicEndpointImportAction.Invalid,
                    Errors = new Dictionary<string, string[]>
                    {
                        ["processor"] = [$"Mock mode needs the built-in '{OpenApiConverter.MockProcessor}' processor: register it with AddResponseTemplateProcessor() or AddBuiltInProcessors()."],
                    },
                };
            }
        }
    }

    /// <summary>
    /// Pairs the operations with the endpoints imported from them before (by origin, then by method and route among the endpoints
    /// of the same document) and decides what to do with each. Endpoints without the document's origin are never touched.
    /// </summary>
    private static List<Planned> Match(List<OpenApiImportedOperation> operations, List<DynamicEndpointDefinition> current, OpenApiConverter.Conversion conversion, OpenApiImportOptions options)
    {
        var fromDocument = current.Where(c => IsFrom(c, conversion.DocumentId)).ToList();
        var matched = new Dictionary<int, DynamicEndpointDefinition>();
        var taken = new HashSet<Guid>();
        var candidates = operations.Select((o, i) => (Operation: o, Index: i))
            .Where(c => c.Operation is { Action: DynamicEndpointImportAction.Create, Definition: not null })
            .ToList();

        // By origin first, so a moved operation isn't paired with an endpoint whose own operation still exists.
        foreach (var (operation, index) in candidates)
        {
            var key = operation.Definition!.Origin!.Operation;
            if (fromDocument.FirstOrDefault(c => !taken.Contains(c.Id) && c.Origin!.Operation == key) is { } existing)
            {
                matched[index] = existing;
                taken.Add(existing.Id);
            }
        }

        // An operation whose operationId changed (or was added) is still found under its method and route.
        foreach (var (operation, index) in candidates.Where(c => !matched.ContainsKey(c.Index)))
        {
            var d = operation.Definition!;
            var route = DynamicEndpointTransfer.RouteKey(d);
            if (route is not null && fromDocument.FirstOrDefault(c => !taken.Contains(c.Id) && c.Method == d.Method && DynamicEndpointTransfer.RouteKey(c) == route) is { } existing)
            {
                matched[index] = existing;
                taken.Add(existing.Id);
            }
        }

        var plan = new List<Planned>();
        foreach (var (operation, index) in candidates)
        {
            var d = operation.Definition!;
            if (matched.TryGetValue(index, out var existing))
            {
                if (options.Mode == DynamicEndpointImportMode.Create)
                {
                    operations[index] = operation with
                    {
                        Action = DynamicEndpointImportAction.Skip,
                        Id = existing.Id,
                        Reason = "Imported before – use mode upsert or sync to update it.",
                    };
                    continue;
                }

                var merged = Merge(existing, d, operation.ProcessorSource);
                operations[index] = operation with
                {
                    Id = existing.Id,
                    Definition = merged,
                    Processor = merged.Processor ?? operation.Processor,
                    ProcessorSource = operation.ProcessorSource == OpenApiProcessorSource.Default ? OpenApiProcessorSource.Kept : operation.ProcessorSource,
                    ProcessorReason = operation.ProcessorSource == OpenApiProcessorSource.Default ? "kept from the existing endpoint" : operation.ProcessorReason,
                };
                plan.Add(new Planned(index, merged, New: false, Delete: false));
                continue;
            }

            var route = DynamicEndpointTransfer.RouteKey(d);
            if (route is not null && current.FirstOrDefault(c => !taken.Contains(c.Id) && c.Method == d.Method && DynamicEndpointTransfer.RouteKey(c) == route) is { } other)
            {
                operations[index] = operation with
                {
                    Action = DynamicEndpointImportAction.Skip,
                    Id = other.Id,
                    Reason = IsFrom(other, conversion.DocumentId)
                        ? "The route belongs to an endpoint imported from another operation of the document."
                        : $"{other.Method} {other.Route} exists already and wasn't imported from this document – it is left alone.",
                };
                continue;
            }

            var created = d with { Id = Guid.CreateVersion7(), Enabled = options.Enabled };
            operations[index] = operation with { Definition = created };
            plan.Add(new Planned(index, created, New: true, Delete: false));
        }

        if (options.Mode == DynamicEndpointImportMode.Sync)
        {
            // Gone from the document – not merely filtered out by Tags, or not mappable.
            foreach (var orphan in DynamicEndpointExport.Sort(fromDocument.Where(c => !taken.Contains(c.Id) && !conversion.OperationKeys.Contains(c.Origin!.Operation ?? ""))))
            {
                operations.Add(new OpenApiImportedOperation
                {
                    Method = orphan.Method,
                    Path = orphan.Route,
                    OperationId = orphan.Origin!.Operation,
                    Action = DynamicEndpointImportAction.Delete,
                    Id = orphan.Id,
                    Reason = "The operation is no longer in the document.",
                    Processor = orphan.Processor,
                    ProcessorSource = OpenApiProcessorSource.Kept,
                    Definition = orphan,
                });
                plan.Add(new Planned(operations.Count - 1, orphan, New: false, Delete: true));
            }
        }

        return plan;
    }

    /// <summary>
    /// The update of an imported endpoint: what the document describes replaces the old content, what admins configure is kept –
    /// the enabled state, tenant, rules, validators, request example, policies, rate limit, caching, and the processor with its
    /// configuration unless the import chose one (extension, tag, option or mock).
    /// </summary>
    internal static DynamicEndpointDefinition Merge(DynamicEndpointDefinition existing, DynamicEndpointDefinition incoming, OpenApiProcessorSource source)
    {
        var validators = existing.Parameters
            .Where(p => p.Validators is { Count: > 0 })
            .GroupBy(p => (p.Name, p.Source))
            .ToDictionary(g => g.Key, g => g.First().Validators);
        var ownsProcessor = source != OpenApiProcessorSource.Default;
        return existing with
        {
            Method = incoming.Method,
            Route = incoming.Route,
            Name = incoming.Name ?? existing.Name,
            Description = incoming.Description ?? existing.Description,
            Group = incoming.Group ?? existing.Group,
            Parameters = incoming.Parameters
                .Select(p => p.Validators is null && validators.TryGetValue((p.Name, p.Source), out var kept) ? p with { Validators = kept } : p)
                .ToList(),
            ResponseSchema = incoming.ResponseSchema ?? existing.ResponseSchema,
            ResponseExample = incoming.ResponseExample ?? existing.ResponseExample,
            RequireAuthorization = incoming.RequireAuthorization,
            AllowAnonymous = incoming.AllowAnonymous,
            Processor = ownsProcessor ? incoming.Processor : existing.Processor,
            ProcessorConfig = ownsProcessor ? incoming.ProcessorConfig : existing.ProcessorConfig,
            Origin = incoming.Origin,
        };
    }

    private static bool IsFrom(DynamicEndpointDefinition d, string? document) =>
        document is not null && d.Origin is { Kind: DynamicEndpointOrigin.OpenApi } origin && string.Equals(origin.Document, document, StringComparison.Ordinal);

    // The transfer does the rest: validation of the whole set (route conflicts included), the rehearsal, writes, history and events.
    private async Task<DynamicEndpointImportResult> RunAsync(List<Planned> plan, List<OpenApiImportedOperation> operations, bool dryRun, CancellationToken cancellationToken)
    {
        var import = new DynamicEndpointExport { Endpoints = plan.Where(p => !p.Delete).Select(p => p.Definition).ToList() };
        var deletes = plan.Where(p => p.Delete).Select(p => p.Definition.Id).ToHashSet();
        if (deletes.Count == 0)
        {
            return await transfer.ImportAsync(import, new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Upsert, DryRun = dryRun }, cancellationToken);
        }

        if (transfer is not DynamicEndpointTransfer scoped)
        {
            throw new InvalidOperationException($"{transfer.GetType().Name} can't sync an OpenAPI import – it can't delete only the document's endpoints.");
        }

        return await scoped.ImportAsync(import, new DynamicEndpointImportOptions { Mode = DynamicEndpointImportMode.Sync, DryRun = dryRun },
            d => deletes.Contains(d.Id), cancellationToken);
    }

    private static void Apply(List<OpenApiImportedOperation> operations, List<Planned> plan, DynamicEndpointImportResult result)
    {
        var items = result.Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (var planned in plan)
        {
            if (!items.TryGetValue(planned.Definition.Id, out var item))
            {
                continue;
            }

            var operation = operations[planned.Index];
            operations[planned.Index] = operation with
            {
                Action = item.Action,
                Changes = item.Changes,
                Errors = item.Errors,
                // New endpoints of a dry run get no id – it would be a different one on the real run.
                Id = planned.New && result.DryRun ? null : item.Id,
                Definition = planned.New && result.DryRun ? planned.Definition with { Id = Guid.Empty } : planned.Definition,
            };
        }
    }

    private sealed record Planned(int Index, DynamicEndpointDefinition Definition, bool New, bool Delete);
}

/// <summary>Reads <c>POST /import/openapi</c>: the document, or <c>{ "document": …, "options": … }</c>, plus the query string.</summary>
internal static class OpenApiImportRequest
{
    /// <exception cref="FormatException">The body or a query value can't be read.</exception>
    public static (JsonNode Document, OpenApiImportOptions Options) Read(JsonNode? body, OpenApiImportQuery query)
    {
        var document = body ?? throw new FormatException("The document is empty.");
        var options = new OpenApiImportOptions();
        if (body is JsonObject envelope && envelope["openapi"] is null && envelope["swagger"] is null && envelope.ContainsKey("document"))
        {
            document = envelope["document"] ?? throw new FormatException("'document' is empty.");
            if (envelope["options"] is JsonObject raw)
            {
                try
                {
                    options = raw.Deserialize<OpenApiImportOptions>(DynamicEndpointsJson.SerializerOptions) ?? options;
                }
                catch (JsonException ex)
                {
                    throw new FormatException($"'options' can't be read: {ex.Message}", ex);
                }
            }
        }

        var byTag = new Dictionary<string, OpenApiProcessorMapping>(options.ProcessorsByTag ?? new Dictionary<string, OpenApiProcessorMapping>(), StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in query.ProcessorByTag ?? [])
        {
            var separator = mapping.LastIndexOf(':');
            if (separator <= 0 || separator == mapping.Length - 1)
            {
                throw new FormatException($"processorByTag '{mapping}' must look like tag:processor.");
            }

            byTag[mapping[..separator].Trim()] = new OpenApiProcessorMapping { Processor = mapping[(separator + 1)..].Trim() };
        }

        var mode = options.Mode;
        if (!string.IsNullOrWhiteSpace(query.Mode) && (!Enum.TryParse(query.Mode, ignoreCase: true, out mode) || !Enum.IsDefined(mode)))
        {
            throw new FormatException($"Unknown mode '{query.Mode}'. Use create, upsert or sync.");
        }

        return (document, options with
        {
            Processor = query.Processor ?? options.Processor,
            RoutePrefix = query.RoutePrefix ?? options.RoutePrefix,
            Group = query.Group ?? options.Group,
            Tags = query.Tag is { Length: > 0 } tags ? tags : options.Tags,
            DocumentId = query.DocumentId ?? options.DocumentId,
            ProcessorsByTag = byTag.Count > 0 ? byTag : null,
            Mode = mode,
            Mock = query.Mock ?? options.Mock,
            Enabled = query.Enabled ?? options.Enabled,
            SkipInvalid = query.SkipInvalid ?? options.SkipInvalid,
            DryRun = query.DryRun ?? options.DryRun,
        });
    }
}

internal sealed record OpenApiImportQuery(
    bool? DryRun,
    string? Processor,
    string? RoutePrefix,
    string? Group,
    string[]? Tag,
    bool? Enabled,
    bool? SkipInvalid,
    string? Mode,
    bool? Mock,
    string[]? ProcessorByTag,
    string? DocumentId);
