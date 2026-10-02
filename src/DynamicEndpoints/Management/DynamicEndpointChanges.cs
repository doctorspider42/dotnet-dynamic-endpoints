using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicEndpoints;

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointChangeKind>))]
public enum DynamicEndpointChangeKind
{
    Created,
    /// <summary>Any change of an existing definition, including enabling and disabling it.</summary>
    Updated,
    Deleted,
}

[JsonConverter(typeof(JsonStringEnumConverter<DynamicEndpointChangeOrigin>))]
public enum DynamicEndpointChangeOrigin
{
    /// <summary>
    /// Made through <see cref="IDynamicEndpointManager"/> of this instance – raised exactly once per change, so this is the
    /// one to audit.
    /// </summary>
    Local,
    /// <summary>
    /// Picked up from the store by a reload: made by another instance (change notification, polling) or directly in the store.
    /// Every other instance raises it, so use it for per-instance work such as invalidating local caches.
    /// </summary>
    Remote,
}

/// <summary>A definition that was created, changed or deleted.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="Id">Id of the definition.</param>
/// <param name="Origin">Whether the change was made by this instance or picked up from the store.</param>
/// <param name="Definition">The new definition; <c>null</c> for <see cref="DynamicEndpointChangeKind.Deleted"/>.</param>
/// <param name="Previous">The definition before the change, when it is known; <c>null</c> for <see cref="DynamicEndpointChangeKind.Created"/>.</param>
public sealed record DynamicEndpointChangedEvent(
    DynamicEndpointChangeKind Kind,
    Guid Id,
    DynamicEndpointChangeOrigin Origin,
    DynamicEndpointDefinition? Definition,
    DynamicEndpointDefinition? Previous);

/// <summary>
/// Runs after definitions changed and the routing table of this instance was updated – e.g. for audit logs or cache invalidation.
/// Register with <c>AddChangeHandler&lt;T&gt;()</c>; handlers are resolved from a new scope and run in registration order.
/// Exceptions are logged and do not undo the change.
/// </summary>
public interface IDynamicEndpointChangeHandler
{
    Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken cancellationToken);
}

internal sealed class DelegateChangeHandler(Func<DynamicEndpointChangedEvent, CancellationToken, Task> handler) : IDynamicEndpointChangeHandler
{
    public Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken cancellationToken) => handler(change, cancellationToken);
}

/// <summary>Message sent to the other instances after definitions were changed.</summary>
/// <param name="InstanceId"><see cref="DynamicEndpointsOptions.InstanceId"/> of the sender – an instance ignores its own messages.</param>
/// <param name="EndpointIds">The changed definitions (empty: unknown). Informational – receivers always re-read the whole store.</param>
public sealed record DynamicEndpointChangeNotification(
    string InstanceId,
    IReadOnlyList<Guid> EndpointIds)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Compact JSON form for transports – well below the 8000 byte limit of PostgreSQL <c>NOTIFY</c>.</summary>
    public string ToJson() =>
        JsonSerializer.Serialize(EndpointIds.Count > 100 ? this with { EndpointIds = [] } : this, Json);

    /// <summary>Parses <see cref="ToJson"/>; <c>null</c> for messages that are not notifications.</summary>
    public static DynamicEndpointChangeNotification? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DynamicEndpointChangeNotification>(json, Json) is { InstanceId: not null } n
                ? n with { EndpointIds = n.EndpointIds ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Transport that tells the other instances about changes right away (e.g. PostgreSQL <c>LISTEN/NOTIFY</c> or Redis pub/sub),
/// so they reload immediately instead of waiting for <see cref="DynamicEndpointsOptions.RefreshInterval"/>. Keep polling
/// configured as a fallback for lost messages. Register with <c>UseChangeNotifier&lt;T&gt;()</c>.
/// </summary>
public interface IDynamicEndpointChangeNotifier
{
    /// <summary>Called after a change was committed and applied locally. Failures are logged; the change itself stands.</summary>
    Task PublishAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken);

    /// <summary>
    /// Receives notifications until <paramref name="cancellationToken"/> is cancelled. Call
    /// <see cref="IDynamicEndpointChangeListener.OnConnectedAsync"/> after every (re)connect – messages may have been missed in the meantime.
    /// If this method throws, it is called again after a short delay.
    /// </summary>
    Task ListenAsync(IDynamicEndpointChangeListener listener, CancellationToken cancellationToken);
}

/// <summary>Receives messages of an <see cref="IDynamicEndpointChangeNotifier"/>. Calls are cheap: reloads are coalesced.</summary>
public interface IDynamicEndpointChangeListener
{
    /// <summary>The subscription is (again) active – the library reloads, in case changes were missed.</summary>
    Task OnConnectedAsync(CancellationToken cancellationToken);

    Task OnNotificationAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken);
}
