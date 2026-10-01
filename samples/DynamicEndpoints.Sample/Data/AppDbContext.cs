using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Sample.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The dynamic endpoint definitions live in the application's own database.
        modelBuilder.ApplyDynamicEndpointsConfiguration();

        modelBuilder.Entity<DocumentRecord>(entity =>
        {
            entity.HasKey(d => d.Id);
            entity.HasIndex(d => d.Collection);
            entity.Property(d => d.Collection).HasMaxLength(64);
        });
    }
}

/// <summary>Schemaless document used by the "collection" processor.</summary>
public sealed class DocumentRecord
{
    public Guid Id { get; set; }

    public string Collection { get; set; } = "";

    public string Data { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}
