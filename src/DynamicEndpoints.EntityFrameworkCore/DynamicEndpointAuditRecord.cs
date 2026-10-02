using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// Table row of an audit entry. Filterable fields are columns, the complete entry (diff and definitions) is JSON. Opt-in: add it to
/// your own DbContext with <see cref="DynamicEndpointsAuditModelBuilderExtensions.ApplyDynamicEndpointsAuditConfiguration"/> – the
/// bundled <see cref="DynamicEndpointsDbContext"/> doesn't contain it.
/// </summary>
public sealed class DynamicEndpointAuditRecord
{
    public Guid Id { get; set; }

    /// <summary>UTC. A <see cref="DateTime"/> rather than a <see cref="DateTimeOffset"/>, so every provider (SQLite too) can filter and sort by it.</summary>
    public DateTime Timestamp { get; set; }

    public string Kind { get; set; } = "";

    public Guid EndpointId { get; set; }

    public string? Tenant { get; set; }

    public string? User { get; set; }

    public string? Method { get; set; }

    public string? Route { get; set; }

    public int Revision { get; set; }

    public string Entry { get; set; } = "";

    internal static DynamicEndpointAuditRecord From(DynamicEndpointAuditEntry entry) => new()
    {
        Id = entry.Id,
        Timestamp = entry.Timestamp.UtcDateTime,
        Kind = entry.Kind.ToString(),
        EndpointId = entry.EndpointId,
        Tenant = entry.Tenant?.ToLowerInvariant(),
        User = entry.User,
        Method = entry.Method,
        Route = entry.Route,
        Revision = entry.Revision,
        Entry = JsonSerializer.Serialize(entry, DynamicEndpointsJson.SerializerOptions),
    };

    internal DynamicEndpointAuditEntry ToEntry() =>
        JsonSerializer.Deserialize<DynamicEndpointAuditEntry>(Entry, DynamicEndpointsJson.SerializerOptions)
            ?? throw new InvalidOperationException($"Stored audit entry '{Id}' is empty.");
}

/// <summary>Mapping of <see cref="DynamicEndpointAuditRecord"/> – add it to your own DbContext and create the table with your migrations.</summary>
public sealed class DynamicEndpointAuditRecordConfiguration(string table = DynamicEndpointAuditRecordConfiguration.DefaultTable, string? schema = null)
    : IEntityTypeConfiguration<DynamicEndpointAuditRecord>
{
    public const string DefaultTable = "DynamicEndpointAudit";

    public void Configure(EntityTypeBuilder<DynamicEndpointAuditRecord> entity)
    {
        entity.ToTable(table, schema);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Kind).HasMaxLength(16).IsRequired();
        entity.Property(e => e.Tenant).HasMaxLength(100);
        entity.Property(e => e.User).HasMaxLength(256);
        entity.Property(e => e.Method).HasMaxLength(16);
        entity.Property(e => e.Route).HasMaxLength(1024);
        entity.Property(e => e.Entry).IsRequired();
        entity.HasIndex(e => e.Timestamp);
        entity.HasIndex(e => new { e.EndpointId, e.Timestamp });
    }
}

public static class DynamicEndpointsAuditModelBuilderExtensions
{
    /// <summary>Adds the audit table to your own DbContext model – create it with your migrations, then use <c>a.ToEntityFramework&lt;TContext&gt;()</c>.</summary>
    public static ModelBuilder ApplyDynamicEndpointsAuditConfiguration(
        this ModelBuilder modelBuilder,
        string table = DynamicEndpointAuditRecordConfiguration.DefaultTable,
        string? schema = null) =>
        modelBuilder.ApplyConfiguration(new DynamicEndpointAuditRecordConfiguration(table, schema));
}

/// <summary>Persistent, queryable audit log in a table of your own DbContext.</summary>
internal sealed class EntityFrameworkDynamicEndpointAuditLog<TContext>(TContext context) : IDynamicEndpointAuditLog
    where TContext : DbContext
{
    private DbSet<DynamicEndpointAuditRecord> Records => context.Set<DynamicEndpointAuditRecord>();

    public async Task WriteAsync(DynamicEndpointAuditEntry entry, CancellationToken cancellationToken)
    {
        Records.Add(DynamicEndpointAuditRecord.From(entry));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DynamicEndpointAuditEntry>> QueryAsync(DynamicEndpointAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var records = Records.AsNoTracking();
        if (query.EndpointId is { } id)
        {
            records = records.Where(r => r.EndpointId == id);
        }

        if (query.Tenant is { } tenant)
        {
            var normalized = tenant.ToLowerInvariant();
            records = records.Where(r => r.Tenant == normalized);
        }

        if (query.User is { } user)
        {
            records = records.Where(r => r.User == user);
        }

        if (query.From is { } from)
        {
            var utc = from.UtcDateTime;
            records = records.Where(r => r.Timestamp >= utc);
        }

        if (query.To is { } to)
        {
            var utc = to.UtcDateTime;
            records = records.Where(r => r.Timestamp <= utc);
        }

        var page = await records
            .OrderByDescending(r => r.Timestamp).ThenByDescending(r => r.Id)
            .Take(Math.Max(query.Limit, 0))
            .ToListAsync(cancellationToken);
        return page.Select(r => r.ToEntry()).ToList();
    }
}
