using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// The EF Core store with history and drafts – used when the model contains <see cref="DynamicEndpointRevisionRecord"/> and
/// <see cref="DynamicEndpointDraftRecord"/>. A revision is only tracked: the write of its definition that follows saves both together.
/// </summary>
internal sealed class EntityFrameworkRevisionStore<TContext>(TContext context, bool saveChanges)
    : EntityFrameworkDynamicEndpointStore<TContext>(context, saveChanges), IDynamicEndpointRevisionStore
    where TContext : DbContext
{
    private DbSet<DynamicEndpointRevisionRecord> Revisions => Context.Set<DynamicEndpointRevisionRecord>();

    private DbSet<DynamicEndpointDraftRecord> Drafts => Context.Set<DynamicEndpointDraftRecord>();

    public override async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // One SaveChanges for the definition, its history and its draft.
        var record = await Context.Set<DynamicEndpointRecord>().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (record is null)
        {
            return false;
        }

        Context.Remove(record);
        Revisions.RemoveRange(await Revisions.Where(r => r.EndpointId == id).ToListAsync(cancellationToken));
        if (await FindTrackedAsync(Drafts, d => d.EndpointId == id, cancellationToken) is { } draft)
        {
            Drafts.Remove(draft);
        }

        if (SavesChanges)
        {
            await Context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task AddRevisionAsync(DynamicEndpointRevision revision, CancellationToken cancellationToken)
    {
        var record = await FindTrackedAsync(Revisions, r => r.EndpointId == revision.EndpointId && r.Revision == revision.Revision, cancellationToken);
        if (record is null)
        {
            Revisions.Add(record = new DynamicEndpointRevisionRecord());
        }

        record.EndpointId = revision.EndpointId;
        record.Revision = revision.Revision;
        record.Kind = revision.Kind.ToString();
        record.Comment = revision.Comment;
        record.SourceRevision = revision.SourceRevision;
        record.SavedAt = revision.SavedAt;
        record.Definition = Serialize(revision.Definition);

        if (revision.Kind == DynamicEndpointRevisionKind.Published &&
            await FindTrackedAsync(Drafts, d => d.EndpointId == revision.EndpointId, cancellationToken) is { } draft)
        {
            Drafts.Remove(draft);
        }

        // Not saved here – the AddAsync/UpdateAsync of the definition that follows saves it in the same SaveChanges.
    }

    public async Task<IReadOnlyList<DynamicEndpointRevision>> GetRevisionsAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var records = await Revisions.AsNoTracking()
            .Where(r => r.EndpointId == endpointId)
            .OrderByDescending(r => r.Revision)
            .ToListAsync(cancellationToken);
        return records.Select(ToRevision).ToList();
    }

    public async Task<DynamicEndpointRevision?> FindRevisionAsync(Guid endpointId, int revision, CancellationToken cancellationToken)
    {
        var record = await FindTrackedAsync(Revisions, r => r.EndpointId == endpointId && r.Revision == revision, cancellationToken, tracking: false);
        return record is null ? null : ToRevision(record);
    }

    public async Task<IReadOnlyList<DynamicEndpointDraft>> GetDraftsAsync(CancellationToken cancellationToken)
    {
        var records = await Drafts.AsNoTracking().ToListAsync(cancellationToken);
        return records.Select(ToDraft).ToList();
    }

    public async Task<DynamicEndpointDraft?> FindDraftAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var record = await FindTrackedAsync(Drafts, d => d.EndpointId == endpointId, cancellationToken, tracking: false);
        return record is null ? null : ToDraft(record);
    }

    public async Task SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken)
    {
        var record = await FindTrackedAsync(Drafts, d => d.EndpointId == draft.EndpointId, cancellationToken);
        if (record is null)
        {
            Drafts.Add(record = new DynamicEndpointDraftRecord { EndpointId = draft.EndpointId });
        }

        record.BaseRevision = draft.BaseRevision;
        record.Method = draft.Definition.Method;
        record.Route = draft.Definition.Route;
        record.Name = draft.Definition.Name;
        record.PublishAt = draft.PublishAt;
        record.Comment = draft.Comment;
        record.CreatedAt = draft.CreatedAt;
        record.UpdatedAt = draft.UpdatedAt;
        record.Definition = Serialize(draft.Definition);
        if (SavesChanges)
        {
            await Context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> DeleteDraftAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var record = await FindTrackedAsync(Drafts, d => d.EndpointId == endpointId, cancellationToken);
        if (record is null)
        {
            return false;
        }

        Drafts.Remove(record);
        if (SavesChanges)
        {
            await Context.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    // Changes of the current unit of work (not saved yet with saveChanges: false) win over the database.
    private async Task<T?> FindTrackedAsync<T>(
        DbSet<T> set, Expression<Func<T, bool>> predicate, CancellationToken cancellationToken, bool tracking = true)
        where T : class
    {
        var match = predicate.Compile();
        if (Context.ChangeTracker.Entries<T>().FirstOrDefault(e => match(e.Entity)) is { } entry)
        {
            return entry.State == EntityState.Deleted ? null : entry.Entity;
        }

        return await (tracking ? set : set.AsNoTracking()).FirstOrDefaultAsync(predicate, cancellationToken);
    }

    private static string Serialize(DynamicEndpointDefinition definition) =>
        JsonSerializer.Serialize(definition, DynamicEndpointsJson.SerializerOptions);

    private static DynamicEndpointDefinition Deserialize(Guid id, string json) =>
        JsonSerializer.Deserialize<DynamicEndpointDefinition>(json, DynamicEndpointsJson.SerializerOptions)
        ?? throw new InvalidOperationException($"Stored revision or draft of dynamic endpoint '{id}' is empty.");

    private static DynamicEndpointRevision ToRevision(DynamicEndpointRevisionRecord record) => new()
    {
        Definition = Deserialize(record.EndpointId, record.Definition) with
        {
            Id = record.EndpointId,
            Revision = record.Revision,
            UpdatedAt = record.SavedAt,
        },
        Kind = Enum.TryParse<DynamicEndpointRevisionKind>(record.Kind, out var kind) ? kind : DynamicEndpointRevisionKind.Updated,
        Comment = record.Comment,
        SourceRevision = record.SourceRevision,
    };

    private static DynamicEndpointDraft ToDraft(DynamicEndpointDraftRecord record) => new()
    {
        Definition = Deserialize(record.EndpointId, record.Definition) with { Id = record.EndpointId, Revision = record.BaseRevision },
        PublishAt = record.PublishAt,
        Comment = record.Comment,
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
    };
}
