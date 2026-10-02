using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.Samples.DraftsHistoryAudit;

/// <summary>
/// The definitions with their revisions and drafts (three tables), plus the audit log table – opt-in, in your own context, so
/// your migrations create it.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder
            .ApplyDynamicEndpointsConfiguration()         // DynamicEndpoints, DynamicEndpointRevisions, DynamicEndpointDrafts
            .ApplyDynamicEndpointsAuditConfiguration();   // DynamicEndpointAudit
}
