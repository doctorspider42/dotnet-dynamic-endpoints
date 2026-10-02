using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.CachingAndRateLimits;

/// <summary>Stores the endpoint definitions – caching and rate limits included, they are part of the definition.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyDynamicEndpointsConfiguration();
}
