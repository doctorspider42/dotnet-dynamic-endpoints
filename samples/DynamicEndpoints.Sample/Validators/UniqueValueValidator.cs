using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using DynamicEndpoints.Sample.Data;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Sample.Validators;

public sealed class UniqueValueConfig
{
    [Required(ErrorMessage = "'collection' is required.")]
    public string? Collection { get; set; }

    [Required(ErrorMessage = "'field' is required.")]
    public string? Field { get; set; }
}

/// <summary>Request-level validator with a database lookup: rejects values already stored in a document collection.</summary>
[DynamicValidator("unique-value",
    Description = "Rejects the request when another document in the collection has the same value of a field.",
    ConfigurationExample = """{ "collection": "notes", "field": "title" }""",
    Targets = DynamicValidatorTargets.Request)]
public sealed class UniqueValueValidator(AppDbContext db) : DynamicValidator<UniqueValueConfig>
{
    protected override async ValueTask ValidateAsync(DynamicValidationContext context, UniqueValueConfig configuration)
    {
        var value = context.Parameters[configuration.Field!]?.ToJsonString();
        if (value is null)
        {
            return;
        }

        // Demo-grade: documents are schemaless JSON, so compare in memory.
        var documents = await db.Documents
            .Where(d => d.Collection == configuration.Collection)
            .Select(d => d.Data)
            .ToListAsync(context.RequestAborted);
        if (documents.Any(data => (JsonNode.Parse(data) as JsonObject)?[configuration.Field!]?.ToJsonString() == value))
        {
            context.AddError(configuration.Field, $"Another {configuration.Collection} entry already uses this {configuration.Field}.");
        }
    }
}
