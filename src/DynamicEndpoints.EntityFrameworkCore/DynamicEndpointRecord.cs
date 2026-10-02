using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

    /// <summary>
    /// <see cref="DynamicEndpointDefinition.Revision"/> – the concurrency token. The column keeps its original name,
    /// so existing databases need no migration.
    /// </summary>
    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string Definition { get; set; } = "";
}

/// <summary>
/// Mapping of <see cref="DynamicEndpointRecord"/> – add it to your own DbContext (<c>modelBuilder.ApplyConfiguration(new DynamicEndpointRecordConfiguration())</c>
/// or <see cref="DynamicEndpointsModelBuilderExtensions.ApplyDynamicEndpointsConfiguration"/>), and your migrations create and evolve the table.
/// </summary>
public sealed class DynamicEndpointRecordConfiguration(string table = DynamicEndpointRecordConfiguration.DefaultTable, string? schema = null)
    : IEntityTypeConfiguration<DynamicEndpointRecord>
{
    public const string DefaultTable = "DynamicEndpoints";

    public void Configure(EntityTypeBuilder<DynamicEndpointRecord> entity)
    {
        entity.ToTable(table, schema);
        entity.HasKey(e => e.Id);
        entity.Property(e => e.Method).HasMaxLength(16).IsRequired();
        entity.Property(e => e.Route).HasMaxLength(1024).IsRequired();
        entity.Property(e => e.Name).HasMaxLength(256);
        entity.Property(e => e.Version).IsConcurrencyToken();
        entity.Property(e => e.Definition).IsRequired();
    }
}

public static class DynamicEndpointsModelBuilderExtensions
{
    /// <summary>Adds the dynamic endpoint table to your own DbContext model – create it with your migrations.</summary>
    public static ModelBuilder ApplyDynamicEndpointsConfiguration(
        this ModelBuilder modelBuilder,
        string table = DynamicEndpointRecordConfiguration.DefaultTable,
        string? schema = null) =>
        modelBuilder.ApplyConfiguration(new DynamicEndpointRecordConfiguration(table, schema));
}

/// <summary>
/// Ready-made context for applications that do not want to add the table to their own context. It ships with its own
/// provider-independent migrations: <c>UseEntityFrameworkStore(…, migrateOnStartup: true)</c> or <c>Database.MigrateAsync()</c>.
/// </summary>
public class DynamicEndpointsDbContext(DbContextOptions<DynamicEndpointsDbContext> options) : DbContext(options)
{
    public DbSet<DynamicEndpointRecord> DynamicEndpoints => Set<DynamicEndpointRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyDynamicEndpointsConfiguration();
}
