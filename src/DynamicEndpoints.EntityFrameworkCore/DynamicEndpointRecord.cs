using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// Table row of a definition. Frequently inspected fields are columns, the full definition is stored as JSON –
/// so the definition model can evolve without schema migrations.
/// </summary>
public sealed class DynamicEndpointRecord
{
    public Guid Id { get; set; }

    public string Method { get; set; } = "";

    public string Route { get; set; } = "";

    public string? Name { get; set; }

    public bool Enabled { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string Definition { get; set; } = "";
}

public static class DynamicEndpointsModelBuilderExtensions
{
    /// <summary>Adds the dynamic endpoint table to your own DbContext model.</summary>
    public static ModelBuilder ApplyDynamicEndpointsConfiguration(this ModelBuilder modelBuilder, string table = "DynamicEndpoints", string? schema = null)
    {
        modelBuilder.Entity<DynamicEndpointRecord>(entity =>
        {
            entity.ToTable(table, schema);
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Method).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Route).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.Name).HasMaxLength(256);
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.Property(e => e.Definition).IsRequired();
        });
        return modelBuilder;
    }
}

/// <summary>Ready-made context for applications that do not want to add the table to their own context.</summary>
public class DynamicEndpointsDbContext(DbContextOptions<DynamicEndpointsDbContext> options) : DbContext(options)
{
    public DbSet<DynamicEndpointRecord> DynamicEndpoints => Set<DynamicEndpointRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyDynamicEndpointsConfiguration();
}
