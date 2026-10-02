namespace DynamicEndpoints;

/// <summary>
/// Optional capability of an <see cref="IDynamicEndpointStore"/>: the history of every definition and drafts that aren't routed
/// until they are published. Implement it on the same class as the endpoint store – the library detects it, and without it the
/// draft, history and rollback operations of <see cref="IDynamicEndpointManager"/> throw <see cref="NotSupportedException"/>.
/// The built-in in-memory and EF Core stores implement it.
/// </summary>
/// <remarks>
/// <see cref="IDynamicEndpointStore.DeleteAsync"/> of an implementing store also removes the history and the draft of the endpoint.
/// </remarks>
public interface IDynamicEndpointRevisionStore
{
    /// <summary>
    /// Records a revision. The library calls it right <em>before</em> the <see cref="IDynamicEndpointStore.AddAsync"/> or
    /// <see cref="IDynamicEndpointStore.UpdateAsync"/> that writes the same definition, so a store with a unit of work (EF Core) can
    /// save both together; a revision with the same number replaces the stored one. A revision of kind
    /// <see cref="DynamicEndpointRevisionKind.Published"/> consumes the endpoint's draft: remove the draft together with it.
    /// </summary>
    Task AddRevisionAsync(DynamicEndpointRevision revision, CancellationToken cancellationToken);

    /// <summary>The revisions of an endpoint, newest first.</summary>
    Task<IReadOnlyList<DynamicEndpointRevision>> GetRevisionsAsync(Guid endpointId, CancellationToken cancellationToken);

    Task<DynamicEndpointRevision?> FindRevisionAsync(Guid endpointId, int revision, CancellationToken cancellationToken);

    Task<IReadOnlyList<DynamicEndpointDraft>> GetDraftsAsync(CancellationToken cancellationToken);

    Task<DynamicEndpointDraft?> FindDraftAsync(Guid endpointId, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the draft of <see cref="DynamicEndpointDraft.EndpointId"/>.</summary>
    Task SaveDraftAsync(DynamicEndpointDraft draft, CancellationToken cancellationToken);

    Task<bool> DeleteDraftAsync(Guid endpointId, CancellationToken cancellationToken);
}
