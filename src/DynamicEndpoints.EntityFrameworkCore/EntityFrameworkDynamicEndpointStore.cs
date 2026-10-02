using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// Store backed by a DbContext. With <c>saveChanges: false</c> writes are only tracked by the context – they are saved by your
/// own <c>SaveChanges</c>, together with your other changes and in your transaction (see <see cref="DbContextDynamicEndpointExtensions.GetDynamicEndpointStore"/>).
/// </summary>
internal class EntityFrameworkDynamicEndpointStore<TContext>(TContext context, bool saveChanges) : IDynamicEndpointStore
    where TContext : DbContext
{
    protected TContext Context => context;

    protected bool SavesChanges => saveChanges;

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
        await SaveAsync(definition.Id, definition.Revision, cancellationToken);
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
        // A record already changed in this unit of work keeps the revision it was loaded with.
        var entry = context.Entry(record);
        if (entry.State == EntityState.Unchanged)
        {
            entry.Property(r => r.Version).OriginalValue = expectedRevision;
        }

        Apply(definition, record);
        await SaveAsync(definition.Id, expectedRevision, cancellationToken);
    }

    public virtual async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        if (saveChanges)
        {
            return await Records.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken) > 0;
        }

        var record = await Records.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (record is null)
        {
            return false;
        }

        Records.Remove(record);
        return true;
    }

    protected async Task SaveAsync(Guid id, int expectedRevision, CancellationToken cancellationToken)
    {
        if (!saveChanges)
        {
            return;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new DynamicEndpointConcurrencyException(id, expectedRevision, null);
        }
    }

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

internal static class EntityFrameworkDynamicEndpointStore
{
    /// <summary>A store for <paramref name="context"/> – with history and drafts when its model has their tables.</summary>
    public static IDynamicEndpointStore Create<TContext>(TContext context, bool saveChanges)
        where TContext : DbContext =>
        context.Model.FindEntityType(typeof(DynamicEndpointRevisionRecord)) is not null &&
        context.Model.FindEntityType(typeof(DynamicEndpointDraftRecord)) is not null
            ? new EntityFrameworkRevisionStore<TContext>(context, saveChanges)
            : new EntityFrameworkDynamicEndpointStore<TContext>(context, saveChanges);
}

public static class DbContextDynamicEndpointExtensions
{
    /// <summary>
    /// A store working on this context (its model must contain the dynamic endpoint table). By default writes are only tracked –
    /// pass it to <see cref="IDynamicEndpointManager.BeginChanges(IDynamicEndpointStore)"/>, call your <c>SaveChanges</c>, then
    /// <see cref="DynamicEndpointChangeSet.ApplyAsync"/>. With <paramref name="saveChanges"/> every write saves the context
    /// right away – inside your transaction when one is open (<c>Database.BeginTransactionAsync()</c>).
    /// </summary>
    public static IDynamicEndpointStore GetDynamicEndpointStore(this DbContext context, bool saveChanges = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        return EntityFrameworkDynamicEndpointStore.Create(context, saveChanges);
    }
}
