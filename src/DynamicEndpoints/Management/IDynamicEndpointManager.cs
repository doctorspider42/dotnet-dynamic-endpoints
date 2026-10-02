namespace DynamicEndpoints;

/// <summary>
/// The single entry point for managing dynamic endpoints. Every change is validated, persisted and applied
/// to the routing table of this instance immediately.
/// </summary>
public interface IDynamicEndpointManager
{
    /// <summary>Processors admins can choose from.</summary>
    IReadOnlyList<DynamicProcessorDescriptor> Processors { get; }

    /// <summary>Custom validators admins can attach to parameters and endpoints.</summary>
    IReadOnlyList<DynamicValidatorDescriptor> Validators { get; }

    Task<IReadOnlyList<DynamicEndpointState>> ListAsync(CancellationToken cancellationToken = default);

    Task<DynamicEndpointState?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <exception cref="DynamicEndpointValidationException">The definition is invalid or clashes with another route.</exception>
    Task<DynamicEndpointDefinition> CreateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces a definition. <see cref="DynamicEndpointDefinition.Revision"/> must match the stored revision
    /// (optimistic concurrency), the returned definition carries the new one.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointConcurrencyException" />
    Task<DynamicEndpointDefinition> UpdateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the definition, or replaces the stored one with the same <see cref="DynamicEndpointDefinition.Id"/> without a
    /// revision check (last writer wins) – handy for syncing definitions from your own model. When the stored definition already has
    /// the same content nothing is written and the revision stays.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    Task<DynamicEndpointDefinition> UpsertAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DynamicEndpointDefinition> SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>Dry run – checks a definition without saving it.</summary>
    Task<DynamicEndpointValidationResult> ValidateAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a unit of work whose changes are written through <paramref name="store"/> – e.g. one bound to your own <c>DbContext</c>
    /// (<c>db.GetDynamicEndpointStore()</c> in the EF Core package), so they are saved by your <c>SaveChanges</c> in your transaction.
    /// The routing table changes only when you call <see cref="DynamicEndpointChangeSet.ApplyAsync"/> after the commit.
    /// </summary>
    DynamicEndpointChangeSet BeginChanges(IDynamicEndpointStore store);

    /// <summary>
    /// Like <see cref="BeginChanges(IDynamicEndpointStore)"/>, with the store resolved from <paramref name="services"/> – pass the request
    /// scope (<c>HttpContext.RequestServices</c>) so a DbContext-based store shares the DbContext (and its transaction) with your code.
    /// </summary>
    DynamicEndpointChangeSet BeginChanges(IServiceProvider services);

    /// <summary>Re-reads all definitions from the store and rebuilds the routing table of this instance.</summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    // ----- drafts, history and rollback – need a store implementing IDynamicEndpointRevisionStore (NotSupportedException otherwise)

    /// <summary>
    /// Saves a draft – validated like a published definition, but not routed until it is published. A definition without an id
    /// drafts a new endpoint. Its <see cref="DynamicEndpointDefinition.Revision"/> is the revision the draft is based on (<c>0</c>: the
    /// current one). Replaces an existing draft; set <see cref="DynamicEndpointDraft.PublishAt"/> to publish it automatically.
    /// </summary>
    /// <exception cref="DynamicEndpointValidationException" />
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointConcurrencyException" />
    Task<DynamicEndpointDraft> SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DynamicEndpointDraft>> ListDraftsAsync(CancellationToken cancellationToken = default);

    Task<DynamicEndpointDraft?> GetDraftAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Drops a draft; the published revision stays.</summary>
    Task<bool> DiscardDraftAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Publishes the draft of an endpoint as its next revision (the first one of a new endpoint).</summary>
    /// <exception cref="DynamicEndpointNotFoundException">There is no draft.</exception>
    /// <exception cref="DynamicEndpointConcurrencyException">The endpoint changed since the draft was based on it.</exception>
    /// <exception cref="DynamicEndpointValidationException" />
    Task<DynamicEndpointDefinition> PublishAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes every draft whose <see cref="DynamicEndpointDraft.PublishAt"/> has come – runs on its own every
    /// <see cref="DynamicEndpointsOptions.ScheduledPublishInterval"/>. A draft that can't be published (invalid, or based on an
    /// outdated revision) is logged and unscheduled, so it doesn't fail again and again; the draft itself stays.
    /// </summary>
    Task<IReadOnlyList<DynamicEndpointDefinition>> PublishDueAsync(CancellationToken cancellationToken = default);

    /// <summary>All revisions of an endpoint, newest first – the published one included.</summary>
    Task<IReadOnlyList<DynamicEndpointRevision>> GetHistoryAsync(Guid id, CancellationToken cancellationToken = default);

    Task<DynamicEndpointRevision?> GetRevisionAsync(Guid id, int revision, CancellationToken cancellationToken = default);

    /// <summary>Publishes the content of an earlier revision again, as the next revision.</summary>
    /// <exception cref="DynamicEndpointNotFoundException" />
    /// <exception cref="DynamicEndpointValidationException">The old revision is no longer valid, e.g. its processor was removed.</exception>
    Task<DynamicEndpointDefinition> RollbackAsync(Guid id, int revision, CancellationToken cancellationToken = default);

    /// <summary>What changed from one revision to another (either may be the older one).</summary>
    /// <exception cref="DynamicEndpointNotFoundException" />
    Task<IReadOnlyList<DynamicEndpointDifference>> DiffAsync(Guid id, int fromRevision, int toRevision, CancellationToken cancellationToken = default);

    /// <summary>What publishing the draft would change – compared with the published revision (everything is new for a new endpoint).</summary>
    /// <exception cref="DynamicEndpointNotFoundException">There is no draft.</exception>
    Task<IReadOnlyList<DynamicEndpointDifference>> DiffDraftAsync(Guid id, CancellationToken cancellationToken = default);
}

public static class DynamicEndpointManagerExtensions
{
    /// <summary>Saves <paramref name="definition"/> as a draft – see <see cref="IDynamicEndpointManager.SaveDraftAsync"/>.</summary>
    public static Task<DynamicEndpointDraft> SaveDraftAsync(
        this IDynamicEndpointManager manager,
        DynamicEndpointDefinition definition,
        DateTimeOffset? publishAt = null,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manager);
        return manager.SaveDraftAsync(new DynamicEndpointDraft { Definition = definition, PublishAt = publishAt, Comment = comment }, cancellationToken);
    }
}
