using System.Collections.Concurrent;
using System.Threading.Channels;

namespace DynamicEndpoints.Testing;

/// <summary>
/// Change notifications within one process – register the same instance in several test hosts (sharing one
/// <see cref="InMemoryDynamicEndpointStore"/>) to test multi-instance behavior without Redis or PostgreSQL.
/// </summary>
public sealed class InMemoryDynamicEndpointChangeNotifier : IDynamicEndpointChangeNotifier
{
    private readonly ConcurrentDictionary<Channel<DynamicEndpointChangeNotification>, byte> _subscribers = new();
    private readonly ConcurrentQueue<DynamicEndpointChangeNotification> _published = new();

    /// <summary>Every notification published so far, oldest first.</summary>
    public IReadOnlyCollection<DynamicEndpointChangeNotification> Published => _published.ToArray();

    public Task PublishAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken)
    {
        _published.Enqueue(notification);
        foreach (var subscriber in _subscribers.Keys)
        {
            subscriber.Writer.TryWrite(notification);
        }

        return Task.CompletedTask;
    }

    public async Task ListenAsync(IDynamicEndpointChangeListener listener, CancellationToken cancellationToken)
    {
        var channel = Channel.CreateUnbounded<DynamicEndpointChangeNotification>();
        _subscribers.TryAdd(channel, 0);
        try
        {
            await listener.OnConnectedAsync(cancellationToken);
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
            {
                await listener.OnNotificationAsync(notification, cancellationToken);
            }
        }
        finally
        {
            _subscribers.TryRemove(channel, out _);
        }
    }
}
