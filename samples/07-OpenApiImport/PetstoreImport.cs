using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.OpenApiImport;

/// <summary>
/// The import from code, on every start: <c>petstore.json</c> (next to the project) in mock mode with <c>Mode = Sync</c>. A sync
/// is idempotent – unchanged operations stay as they are – so the file is the source of truth: change it and restart, and the
/// endpoints follow (new operations created, changed ones updated, removed ones deleted). It's a seeder because seeders run
/// after the stored definitions were loaded.
/// </summary>
public sealed class PetstoreImport(IDynamicEndpointOpenApiImporter importer, IWebHostEnvironment environment, ILogger<PetstoreImport> logger)
    : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "petstore.json");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken))!;

        var result = await importer.ImportAsync(document, new OpenApiImportOptions
        {
            Mock = true,                              // every operation answers with its documented response (built-in "response")
            Mode = DynamicEndpointImportMode.Sync,    // create, update, and delete what's gone from the document
            Enabled = true,                           // imported endpoints are disabled by default – review first
            // The group of each endpoint is the operation's first tag (pets, store); DocumentId defaults to info.title.
        }, cancellationToken);

        // Operations with x-dynamic-endpoints-processor (GET /store/inventory) keep their processor, even in mock mode.
        logger.LogInformation(
            "Imported {Document}: {Created} created, {Updated} updated, {Deleted} deleted, {Unchanged} unchanged, {Invalid} invalid.",
            result.Document, result.Created, result.Updated, result.Deleted, result.Unchanged, result.Invalid);
        foreach (var operation in result.Operations.Where(o => o.Unmapped.Count > 0))
        {
            logger.LogInformation("{Method} {Path}: not mapped – {Unmapped}", operation.Method, operation.Path, string.Join("; ", operation.Unmapped));
        }
    }
}
