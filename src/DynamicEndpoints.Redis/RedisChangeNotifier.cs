using DynamicEndpoints;
using DynamicEndpoints.Redis;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace DynamicEndpoints.Redis
{
    public sealed class RedisChangeNotificationOptions
    {
        /// <summary>Pub/sub channel shared by all instances. Default <c>dynamic-endpoints</c>.</summary>
        public string Channel { get; set; } = "dynamic-endpoints";

        /// <summary>
        /// StackExchange.Redis configuration string (e.g. <c>localhost:6379</c>). When empty, the <see cref="IConnectionMultiplexer"/>
        /// registered in DI is used.
        /// </summary>
        public string? Configuration { get; set; }
    }

    internal sealed class RedisChangeNotifier(
        RedisChangeNotificationOptions options,
        IServiceProvider services,
        ILogger<RedisChangeNotifier> logger) : IDynamicEndpointChangeNotifier, IAsyncDisposable
    {
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private IConnectionMultiplexer? _connection;
        private bool _ownsConnection;

        private RedisChannel Channel => RedisChannel.Literal(options.Channel);

        public async Task PublishAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken)
        {
            var connection = await ConnectAsync(cancellationToken);
            await connection.GetSubscriber().PublishAsync(Channel, notification.ToJson());
        }

        public async Task ListenAsync(IDynamicEndpointChangeListener listener, CancellationToken cancellationToken)
        {
            var connection = await ConnectAsync(cancellationToken);
            var subscriber = connection.GetSubscriber();

            // The multiplexer resubscribes by itself after a reconnect; messages sent while it was gone are lost – reload then.
            void OnRestored(object? sender, ConnectionFailedEventArgs e)
            {
                if (e.ConnectionType == ConnectionType.Subscription)
                {
                    _ = listener.OnConnectedAsync(cancellationToken);
                }
            }

            connection.ConnectionRestored += OnRestored;
            var queue = await subscriber.SubscribeAsync(Channel);
            try
            {
                await listener.OnConnectedAsync(cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var message = await queue.ReadAsync(cancellationToken);
                    if (DynamicEndpointChangeNotification.Parse(message.Message.ToString()) is { } notification)
                    {
                        await listener.OnNotificationAsync(notification, cancellationToken);
                    }
                    else
                    {
                        logger.LogDebug("Ignoring a message on {Channel} that is no change notification.", options.Channel);
                    }
                }
            }
            finally
            {
                connection.ConnectionRestored -= OnRestored;
                await queue.UnsubscribeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ownsConnection && _connection is not null)
            {
                await _connection.DisposeAsync();
            }

            _connectLock.Dispose();
        }

        private async ValueTask<IConnectionMultiplexer> ConnectAsync(CancellationToken cancellationToken)
        {
            if (_connection is { } existing)
            {
                return existing;
            }

            await _connectLock.WaitAsync(cancellationToken);
            try
            {
                if (_connection is null)
                {
                    if (string.IsNullOrWhiteSpace(options.Configuration))
                    {
                        _connection = services.GetService(typeof(IConnectionMultiplexer)) as IConnectionMultiplexer
                            ?? throw new InvalidOperationException(
                                "Redis change notifications need RedisChangeNotificationOptions.Configuration or an IConnectionMultiplexer registered in DI.");
                    }
                    else
                    {
                        _connection = await ConnectionMultiplexer.ConnectAsync(options.Configuration);
                        _ownsConnection = true;
                    }
                }

                return _connection;
            }
            finally
            {
                _connectLock.Release();
            }
        }
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class DynamicEndpointsRedisExtensions
    {
        /// <summary>
        /// Tells the other instances about every change through Redis pub/sub, so they reload right away. Uses the
        /// <see cref="IConnectionMultiplexer"/> from DI unless <see cref="RedisChangeNotificationOptions.Configuration"/> is set.
        /// Keep <c>RefreshInterval</c> as a fallback – pub/sub does not deliver messages sent while an instance was disconnected.
        /// </summary>
        public static IDynamicEndpointsBuilder UseRedisChangeNotifications(
            this IDynamicEndpointsBuilder builder,
            Action<RedisChangeNotificationOptions>? configure = null)
        {
            var options = new RedisChangeNotificationOptions();
            configure?.Invoke(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Channel);

            builder.Services.Replace(ServiceDescriptor.Singleton<IDynamicEndpointChangeNotifier>(sp => new RedisChangeNotifier(
                options, sp, sp.GetRequiredService<ILogger<RedisChangeNotifier>>())));
            return builder;
        }

        /// <summary>Redis pub/sub change notifications through a connection to <paramref name="configuration"/> (e.g. <c>localhost:6379</c>).</summary>
        public static IDynamicEndpointsBuilder UseRedisChangeNotifications(this IDynamicEndpointsBuilder builder, string configuration, string channel = "dynamic-endpoints") =>
            builder.UseRedisChangeNotifications(o =>
            {
                o.Configuration = configuration;
                o.Channel = channel;
            });
    }
}
