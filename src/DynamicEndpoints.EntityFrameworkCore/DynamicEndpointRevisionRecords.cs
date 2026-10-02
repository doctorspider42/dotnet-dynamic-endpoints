using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>Table row of a <see cref="DynamicEndpointRevision"/> – one per saved revision of a definition.</summary>
public sealed class DynamicEndpointRevisionRecord
{
    public Guid EndpointId { get; set; }

    public int Revision { get; set; }

    /// <summary><see cref="DynamicEndpointRevisionKind"/> as text.</summary>
    public string Kind { get; set; } = "";

    public string? Comment { get; set; }

    public int? SourceRevision { get; set; }

    public DateTimeOffset SavedAt { get; set; }

    public string Definition { get; set; } = "";
}

/// <summary>Table row of a <see cref="DynamicEndpointDraft"/> – at most one per endpoint.</summary>
public sealed class DynamicEndpointDraftRecord
{
    public Guid EndpointId { get; set; }

    public int BaseRevision { get; set; }

    public string Method { get; set; } = "";

    public string Route { get; set; } = "";

    public string? Name { get; set; }

    public DateTimeOffset? PublishAt { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string Definition { get; set; } = "";
}

/// <summary>Mapping of <see cref="DynamicEndpointRevisionRecord"/> – the history of the definitions.</summary>
public sealed class DynamicEndpointRevisionRecordConfiguration(string table = DynamicEndpointRevisionRecordConfiguration.DefaultTable, string? schema = null)
    : IEntityTypeConfiguration<DynamicEndpointRevisionRecord>
{
    public const string DefaultTable = "DynamicEndpointRevisions";

    public void Configure(EntityTypeBuilder<DynamicEndpointRevisionRecord> entity)
    {
        entity.ToTable(table, schema);
        entity.HasKey(e => new { e.EndpointId, e.Revision });
        entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
        entity.Property(e => e.Comment).HasMaxLength(1024);
        entity.Property(e => e.Definition).IsRequired();
    }
}

/// <summary>Mapping of <see cref="DynamicEndpointDraftRecord"/> – drafts that aren't routed until they are published.</summary>
public sealed class DynamicEndpointDraftRecordConfiguration(string table = DynamicEndpointDraftRecordConfiguration.DefaultTable, string? schema = null)
    : IEntityTypeConfiguration<DynamicEndpointDraftRecord>
{
    public const string DefaultTable = "DynamicEndpointDrafts";

    public void Configure(EntityTypeBuilder<DynamicEndpointDraftRecord> entity)
    {
        entity.ToTable(table, schema);
        entity.HasKey(e => e.EndpointId);
        entity.Property(e => e.EndpointId).ValueGeneratedNever();
        entity.Property(e => e.Method).HasMaxLength(16).IsRequired();
        entity.Property(e => e.Route).HasMaxLength(1024).IsRequired();
        entity.Property(e => e.Name).HasMaxLength(256);
        entity.Property(e => e.Comment).HasMaxLength(1024);
        entity.Property(e => e.Definition).IsRequired();
    }
}
