using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

internal sealed class EntityFrameworkDynamicEndpointStore<TContext>(TContext context) : IDynamicEndpointStore
    where TContext : DbContext
{
    private DbSet<DynamicEndpointRecord> Records => context.Set<DynamicEndpointRecord>();

    public async Task<IReadOnlyList<DynamicEndpointDefinition>> GetAllAsync(CancellationToken cancellationToken)
    {
        var records = await Records.AsNoTracking().ToListAsync(cancellationToken);
        return records.Select(ToDefinition).ToList();
    }

    public async Task<DynamicEndpointDefinition?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var record = await Records.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return record is null ? null : ToDefinition(record);
    }

    public async Task AddAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        Records.Add(Apply(definition, new DynamicEndpointRecord()));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(DynamicEndpointDefinition definition, int expectedRevision, CancellationToken cancellationToken)
    {
        var record = await Records.FirstOrDefaultAsync(r => r.Id == definition.Id, cancellationToken)
            ?? throw new DynamicEndpointNotFoundException(definition.Id);
        if (record.Version != expectedRevision)
        {
            throw new DynamicEndpointConcurrencyException(definition.Id, expectedRevision, record.Version);
        }

        // The revision column is a concurrency token – the UPDATE only succeeds if nobody bumped it in the meantime.
        context.Entry(record).Property(r => r.Version).OriginalValue = expectedRevision;
        Apply(definition, record);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DynamicEndpointConcurrencyException(definition.Id, expectedRevision, null);
        }
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken) =>
        await Records.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;

    private static DynamicEndpointRecord Apply(DynamicEndpointDefinition definition, DynamicEndpointRecord record)
    {
        record.Id = definition.Id;
        record.Method = definition.Method;
        record.Route = definition.Route;
        record.Name = definition.Name;
        record.Enabled = definition.Enabled;
        record.Version = definition.Revision;
        record.CreatedAt = definition.CreatedAt;
        record.UpdatedAt = definition.UpdatedAt;
        record.Definition = JsonSerializer.Serialize(definition, DynamicEndpointsJson.SerializerOptions);
        return record;
    }

    private static DynamicEndpointDefinition ToDefinition(DynamicEndpointRecord record)
    {
        var definition = JsonSerializer.Deserialize<DynamicEndpointDefinition>(record.Definition, DynamicEndpointsJson.SerializerOptions)
            ?? throw new InvalidOperationException($"Stored definition of dynamic endpoint '{record.Id}' is empty.");

        // Columns are authoritative for the fields the library manages.
        return definition with
        {
            Id = record.Id,
            Enabled = record.Enabled,
            Revision = record.Version,
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }
}
