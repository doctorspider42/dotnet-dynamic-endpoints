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

        group.MapGet("/{id:guid}/snippets", async Task<IResult> (Guid id, string? baseUrl, HttpContext context, IDynamicEndpointManager manager,
                IDynamicEndpointSnippetGenerator snippets, CancellationToken ct) =>
            {
                if (ResolveBaseUrl(context, baseUrl) is not { } url)
                {
                    return BadBaseUrl();
                }

                return await manager.GetAsync(id, ct) is { } state
                    ? TypedResults.Ok(snippets.Generate(state.Definition, url))
                    : TypedResults.NotFound();
            })
            .WithSummary("Generates an example request and curl, HTTPie and C# snippets for an endpoint.")
            .Produces<DynamicEndpointSnippets>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/snippets", (DynamicEndpointDefinition definition, string? baseUrl, HttpContext context, IDynamicEndpointSnippetGenerator snippets) =>
                ResolveBaseUrl(context, baseUrl) is { } url ? TypedResults.Ok(snippets.Generate(definition, url)) : BadBaseUrl())
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
