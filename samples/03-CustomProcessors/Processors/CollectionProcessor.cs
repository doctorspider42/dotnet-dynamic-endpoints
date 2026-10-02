using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.CustomProcessors.Processors;

public enum CollectionAction
{
    Insert,
    List,
    Get,
    Delete,
}

public sealed class CollectionConfig
{
    [Required(ErrorMessage = "'collection' is required.")]
    [RegularExpression("^[a-z][a-z0-9-]{1,63}$", ErrorMessage = "'collection' must be a lowercase slug.")]
    public string? Collection { get; set; }

    public CollectionAction Action { get; set; } = CollectionAction.Insert;
}

/// <summary>
/// Tiny document store on top of the application's database: an endpoint defined in the admin panel
/// becomes a working CRUD operation, with all input validation done by the definition.
/// Get/Delete read the document id from the "id" parameter; List honours an optional "limit" parameter.
/// </summary>
[DynamicProcessor("collection",
    Description = "Stores and reads JSON documents in a named collection (Insert, List, Get, Delete). Get/Delete use the 'id' parameter, List an optional 'limit'.",
    ConfigurationExample = """{ "collection": "notes", "action": "Insert" }""")]
public sealed class CollectionProcessor(AppDbContext db, TimeProvider timeProvider) : DynamicEndpointProcessor<CollectionConfig>
{
    protected override async Task<IResult> ProcessAsync(DynamicRequest request, CollectionConfig configuration)
    {
        var collection = configuration.Collection!;
        var ct = request.RequestAborted;

        switch (configuration.Action)
        {
            case CollectionAction.Insert:
                var record = new DocumentRecord
                {
                    Id = Guid.CreateVersion7(),
                    Collection = collection,
                    Data = request.Parameters.ToJsonString(),
                    CreatedAt = timeProvider.GetUtcNow(),
                };
                db.Documents.Add(record);
                await db.SaveChangesAsync(ct);
                return Results.Created($"{request.HttpContext.Request.Path}/{record.Id}", ToResponse(record));

            case CollectionAction.List:
                var limit = request.Get<int?>("limit") ?? 50;
                var records = await db.Documents
                    .Where(d => d.Collection == collection)
                    .OrderByDescending(d => d.Id)
                    .Take(limit)
                    .ToListAsync(ct);
                return Results.Ok(records.Select(ToResponse));

            case CollectionAction.Get:
                var found = await db.Documents.FirstOrDefaultAsync(d => d.Collection == collection && d.Id == request.GetRequired<Guid>("id"), ct);
                return found is null ? Results.NotFound() : Results.Ok(ToResponse(found));

            case CollectionAction.Delete:
                var id = request.GetRequired<Guid>("id");
                var deleted = await db.Documents.Where(d => d.Collection == collection && d.Id == id).ExecuteDeleteAsync(ct);
                return deleted > 0 ? Results.NoContent() : Results.NotFound();

            default:
                return Results.StatusCode(StatusCodes.Status501NotImplemented);
        }
    }

    private static JsonObject ToResponse(DocumentRecord record)
    {
        var data = JsonNode.Parse(record.Data) as JsonObject ?? [];
        data["id"] = record.Id;
        data["createdAt"] = record.CreatedAt;
        return data;
    }
}
