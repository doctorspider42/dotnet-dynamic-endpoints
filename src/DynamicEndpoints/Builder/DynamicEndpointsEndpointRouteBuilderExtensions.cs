using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints;
using DynamicEndpoints.Audit;
using DynamicEndpoints.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

        group.MapGet("/info", (HttpContext http, IOptions<DynamicEndpointsOptions> options, IEnumerable<IDynamicEndpointsTextFormat> formats) =>
                Info(http, options.Value, formats))
            .WithSummary("What this admin API supports – tenancy, audit log, drafts and history, export formats.");

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

        group.MapGet("/{id:guid}/snippets", async Task<IResult> (Guid id, string? baseUrl, string? tenant, HttpContext context,
                IDynamicEndpointManager manager, IDynamicEndpointSnippetGenerator snippets, CancellationToken ct) =>
            {
                if (ResolveBaseUrl(context, baseUrl) is not { } url)
                {
                    return BadBaseUrl();
                }

                return await manager.GetAsync(id, ct) is { } state
                    ? TypedResults.Ok(snippets.Generate(state.Definition, url, SnippetTenant(context, tenant)))
                    : TypedResults.NotFound();
            })
            .WithSummary("Generates an example request and curl, HTTPie and C# snippets for an endpoint (?tenant= for a shared endpoint with multi-tenancy).")
            .Produces<DynamicEndpointSnippets>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/snippets", (DynamicEndpointDefinition definition, string? baseUrl, string? tenant, HttpContext context,
                IDynamicEndpointSnippetGenerator snippets) =>
                ResolveBaseUrl(context, baseUrl) is { } url
                    ? TypedResults.Ok(snippets.Generate(OwnTenant(context, definition), url, SnippetTenant(context, tenant)))
                    : BadBaseUrl())
            .WithSummary("Generates an example request and snippets for an unsaved definition (editor preview).")
            .Produces<DynamicEndpointSnippets>();

        group.MapGet("/export", async (HttpContext context, Guid[]? id, string? format, IDynamicEndpointTransfer transfer,
                IEnumerable<IDynamicEndpointsTextFormat> formats, CancellationToken ct) =>
            {
                if (DynamicEndpointsTextFormats.ForResponse(formats, format, context.Request.Headers.Accept) is not { } writer)
                {
                    return UnknownFormat(formats);
                }

                var export = await transfer.ExportAsync(id, ct);
                return Results.Text(writer.Write(export.ToJsonNode()), writer.MediaTypes[0]);
            })
            .WithSummary("Exports all definitions, or those given with ?id=, in the stable export format (?format=json|yaml).")
            .Produces<DynamicEndpointExport>(contentType: "application/json");

        group.MapPost("/import", async Task<IResult> (HttpContext context, string? mode, bool? dryRun, IDynamicEndpointTransfer transfer,
                IEnumerable<IDynamicEndpointsTextFormat> formats, CancellationToken ct) =>
            {
                if (!TryParseMode(mode, out var importMode))
                {
                    return TypedResults.Problem($"Unknown mode '{mode}'. Use create, upsert or sync.", statusCode: StatusCodes.Status400BadRequest);
                }

                DynamicEndpointExport import;
                try
                {
                    var text = await ReadBodyAsync(context, ct);
                    import = DynamicEndpointExport.FromJsonNode(DynamicEndpointsTextFormats.ForContentType(formats, context.Request.ContentType).Parse(text));
                }
                catch (FormatException ex)
                {
                    return TypedResults.Problem(ex.Message, title: "The import could not be read.", statusCode: StatusCodes.Status400BadRequest);
                }

                var result = await transfer.ImportAsync(import, new DynamicEndpointImportOptions { Mode = importMode, DryRun = dryRun ?? false }, ct);
                return TypedResults.Json(result, statusCode: result.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
            })
            .WithSummary("Imports exported definitions (?mode=create|upsert|sync, ?dryRun=true). 422 when any endpoint is invalid – nothing is written then.")
            .Accepts<DynamicEndpointExport>("application/json", "application/yaml")
            .Produces<DynamicEndpointImportResult>()
            .Produces<DynamicEndpointImportResult>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/import/openapi", async Task<IResult> (HttpContext context, bool? dryRun, string? processor, string? routePrefix,
                string? group, [FromQuery] string[]? tag, bool? enabled, bool? skipInvalid, IDynamicEndpointOpenApiImporter importer,
                IEnumerable<IDynamicEndpointsTextFormat> formats, CancellationToken ct) =>
            {
                OpenApiImportResult result;
                try
                {
                    var text = await ReadBodyAsync(context, ct);
                    var document = DynamicEndpointsTextFormats.ForContentType(formats, context.Request.ContentType).Parse(text)
                        ?? throw new FormatException("The document is empty.");
                    result = await importer.ImportAsync(document, new OpenApiImportOptions
                    {
                        DryRun = dryRun ?? false,
                        Processor = processor,
                        RoutePrefix = routePrefix,
                        Group = group,
                        Tags = tag is { Length: > 0 } ? tag : null,
                        Enabled = enabled ?? false,
                        SkipInvalid = skipInvalid ?? false,
                    }, ct);
                }
                catch (FormatException ex)
                {
                    return TypedResults.Problem(ex.Message, title: "The OpenAPI document could not be read.", statusCode: StatusCodes.Status400BadRequest);
                }

                return TypedResults.Json(result, statusCode: result.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity);
            })
            .WithSummary("Creates endpoint skeletons from an OpenAPI 3.x document (JSON, or YAML with DynamicEndpoints.Yaml). ?dryRun=true reports what would be created and what couldn't be mapped.")
            .Accepts<JsonObject>("application/json", "application/yaml")
            .Produces<OpenApiImportResult>()
            .Produces<OpenApiImportResult>(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        group.MapPost("/reload", async (IDynamicEndpointManager manager, CancellationToken ct) =>
            {
                await manager.ReloadAsync(ct);
                return TypedResults.NoContent();
            })
            .WithSummary("Re-reads all definitions from the store.");

        MapRevisions(group, root);
        DynamicEndpointsAuditApi.Map(group);
        foreach (var extension in endpoints.ServiceProvider.GetServices<IDynamicEndpointsAdminApiExtension>())
        {
            extension.Map(group);
        }

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
                context.Arguments[i] = context.Arguments[i] switch
                {
                    IDynamicEndpointManager manager => manager.ForTenant(tenant),
                    DynamicEndpointTransfer transfer => transfer.ForTenant(tenant),
                    DynamicEndpointOpenApiImporter importer => importer.ForTenant(tenant),
                    IDynamicEndpointTransfer or IDynamicEndpointOpenApiImporter => throw DynamicEndpointTenantExtensions.NotTenantAware(context.Arguments[i]!),
                    var argument => argument,
                };
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

    // The application's own address by default; an explicit base URL must be absolute http(s).
    private static string? ResolveBaseUrl(HttpContext context, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}";
        }

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? baseUrl.TrimEnd('/') : null;
    }

    private static bool TryParseMode(string? mode, out DynamicEndpointImportMode result)
    {
        result = DynamicEndpointImportMode.Upsert;
        return string.IsNullOrWhiteSpace(mode) || (Enum.TryParse(mode, ignoreCase: true, out result) && Enum.IsDefined(result));
    }

    private static async Task<string> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(context.Request.Body);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static IResult UnknownFormat(IEnumerable<IDynamicEndpointsTextFormat> formats) =>
        TypedResults.Problem($"Unknown format. Available: {string.Join(", ", formats.Select(f => f.Name))}.", statusCode: StatusCodes.Status400BadRequest);

    private static IResult BadBaseUrl() =>
        TypedResults.Problem("'baseUrl' must be an absolute http or https URL.", statusCode: StatusCodes.Status400BadRequest);

    // Tenant of a tenant admin API request, for handlers that don't go through the manager.
    internal const string AdminTenantKey = "DynamicEndpoints.AdminTenant";

    /// <summary>The tenant of a tenant admin API request; <c>null</c> in the full admin API.</summary>
    internal static string? AdminTenant(HttpContext http) =>
        http.Items.TryGetValue(AdminTenantKey, out var tenant) ? tenant as string : null;

    // A tenant's admin API always shows requests of its tenant; otherwise ?tenant=, else the definition's own tenant.
    // In a tenant admin API an unsaved definition belongs to the API's tenant, whatever its body says – like when it is saved.
    private static DynamicEndpointDefinition OwnTenant(HttpContext http, DynamicEndpointDefinition definition) =>
        http.Items.TryGetValue(AdminTenantKey, out var scoped) && scoped is string own ? definition with { Tenant = own } : definition;

    private static string? SnippetTenant(HttpContext http, string? tenant) =>
        http.Items.TryGetValue(AdminTenantKey, out var scoped) && scoped is string own ? own
        : string.IsNullOrWhiteSpace(tenant) ? null
        : tenant;

    private static DynamicEndpointsAdminInfo Info(HttpContext http, DynamicEndpointsOptions options, IEnumerable<IDynamicEndpointsTextFormat> formats)
    {
        var services = http.RequestServices;
        var tenancy = options.Tenancy;
        return new DynamicEndpointsAdminInfo
        {
            Tenancy = tenancy.Enabled
                ? new DynamicEndpointsAdminTenancyInfo(tenancy.RoutePrefix, tenancy.RouteParameter,
                    services.GetServices<IDynamicEndpointTenantResolver>().OfType<HeaderTenantResolver>().FirstOrDefault()?.HeaderName)
                : null,
            Tenant = http.Items.TryGetValue(AdminTenantKey, out var tenant) ? tenant as string : null,
            Revisions = services.GetService<IDynamicEndpointStore>() is IDynamicEndpointRevisionStore,
            AuditLog = services.GetService<IDynamicEndpointAuditLog>() is not null,
            Formats = formats.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Features = services.GetServices<IDynamicEndpointsAdminApiExtension>().SelectMany(e => e.Features(http))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

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
        catch (NotSupportedException ex)
        {
            return TypedResults.Problem(ex.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
    }
}
