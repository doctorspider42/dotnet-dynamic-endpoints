using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.Observability;

/// <summary>Stores the endpoint definitions.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyDynamicEndpointsConfiguration();
}
