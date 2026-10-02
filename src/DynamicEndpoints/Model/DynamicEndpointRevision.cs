using System.Text.Json.Serialization;

namespace DynamicEndpoints;

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointRevisionKind>))]
public enum DynamicEndpointRevisionKind
{
    /// <summary>The first revision, saved directly (not from a draft).</summary>
    Created,
    /// <summary>Saved directly: update or upsert.</summary>
    Updated,
    Enabled,
    Disabled,
    /// <summary>A draft was published – the first revision of a new endpoint, or the next one of an existing endpoint.</summary>
    Published,
    /// <summary>The content of an earlier revision (<see cref="DynamicEndpointRevision.SourceRevision"/>) was restored.</summary>
    RolledBack,
}

/// <summary>A saved revision of a definition – the history of an endpoint, for diffs and rollbacks.</summary>
public sealed record DynamicEndpointRevision
{
    /// <summary>The definition exactly as it was routed in this revision.</summary>
    public required DynamicEndpointDefinition Definition { get; init; }

    public Guid EndpointId => Definition.Id;

    /// <summary><see cref="DynamicEndpointDefinition.Revision"/> of <see cref="Definition"/>.</summary>
    public int Revision => Definition.Revision;

    /// <summary>When the revision was saved (<see cref="DynamicEndpointDefinition.UpdatedAt"/>).</summary>
    public DateTimeOffset SavedAt => Definition.UpdatedAt;

    public DynamicEndpointRevisionKind Kind { get; init; }

    /// <summary>Optional note, e.g. the comment of the published draft.</summary>
    public string? Comment { get; init; }

    /// <summary>For <see cref="DynamicEndpointRevisionKind.RolledBack"/>: the revision whose content was restored.</summary>
    public int? SourceRevision { get; init; }
}

/// <summary>
/// A change of an endpoint that is saved but not routed until it is published – by hand
/// (<see cref="IDynamicEndpointManager.PublishAsync"/>) or at <see cref="PublishAt"/>. An endpoint has at most one draft.
/// </summary>
public sealed record DynamicEndpointDraft
{
    /// <summary>
    /// The definition to publish. Its <see cref="DynamicEndpointDefinition.Revision"/> is the published revision the draft is based
    /// on (<see cref="BaseRevision"/>); when saving, <c>0</c> means "the current one".
    /// </summary>
    public required DynamicEndpointDefinition Definition { get; init; }

    public Guid EndpointId => Definition.Id;

    /// <summary>
    /// Published revision the draft is based on, <c>0</c> for an endpoint that was never published. Publishing fails with
    /// <see cref="DynamicEndpointConcurrencyException"/> when the endpoint changed in the meantime – save the draft again on top of
    /// the current revision to resolve it.
    /// </summary>
    public int BaseRevision => Definition.Revision;

    /// <summary>When set, the draft is published automatically at this time (see <see cref="DynamicEndpointsOptions.ScheduledPublishInterval"/>).</summary>
    public DateTimeOffset? PublishAt { get; init; }

    /// <summary>Optional note, kept as <see cref="DynamicEndpointRevision.Comment"/> of the published revision.</summary>
    public string? Comment { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
