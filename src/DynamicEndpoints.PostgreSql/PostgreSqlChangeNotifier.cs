using DynamicEndpoints;
using DynamicEndpoints.PostgreSql;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace DynamicEndpoints.PostgreSql
{
    public sealed class PostgreSqlChangeNotificationOptions
    {
        /// <summary>
        /// <c>LISTEN/NOTIFY</c> channel shared by all instances. Default <c>dynamic_endpoints</c>. Case-sensitive, at most 63 bytes.
        /// </summary>
        public string Channel { get; set; } = "dynamic_endpoints";

        /// <summary>Connection string. When empty, the <see cref="NpgsqlDataSource"/> registered in DI (<c>AddNpgsqlDataSource</c>) is used.</summary>
        public string? ConnectionString { get; set; }

        /// <summary>
        /// How often the listening connection is checked while it is idle, so a dead connection is noticed and replaced.
        /// Default 30 seconds.
        /// </summary>
        public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    }

    internal sealed class PostgreSqlChangeNotifier(
        PostgreSqlChangeNotificationOptions options,
        IServiceProvider services,
        ILogger<PostgreSqlChangeNotifier> logger) : IDynamicEndpointChangeNotifier, IAsyncDisposable
    {
        private readonly Lock _lock = new();
        private NpgsqlDataSource? _dataSource;
        private bool _ownsDataSource;

        public async Task PublishAsync(DynamicEndpointChangeNotification notification, CancellationToken cancellationToken)
        {
            await using var command = DataSource.CreateCommand("SELECT pg_notify(@channel, @payload)");
            command.Parameters.AddWithValue("channel", options.Channel);
            command.Parameters.AddWithValue("payload", notification.ToJson());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        public async Task ListenAsync(IDynamicEndpointChangeListener listener, CancellationToken cancellationToken)
        {
            await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
            var received = new List<DynamicEndpointChangeNotification>();
            connection.Notification += (_, e) =>
            {
                if (e.Channel != options.Channel)
                {
                    return;
                }

                if (DynamicEndpointChangeNotification.Parse(e.Payload) is { } notification)
                {
                    received.Add(notification);
                }
                else
                {
                    logger.LogDebug("Ignoring a message on {Channel} that is no change notification.", options.Channel);
                }
            };

            await using (var listen = new NpgsqlCommand($"LISTEN {QuoteIdentifier(options.Channel)}", connection))
            {
                await listen.ExecuteNonQueryAsync(cancellationToken);
            }

            await listener.OnConnectedAsync(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                // Notifications are raised synchronously during WaitAsync; hand them over after it returned.
                if (!await connection.WaitAsync(options.KeepAliveInterval, cancellationToken))
                {
                    await using var ping = new NpgsqlCommand("SELECT 1", connection);
                    await ping.ExecuteNonQueryAsync(cancellationToken);
                }

                foreach (var notification in received)
                {
                    await listener.OnNotificationAsync(notification, cancellationToken);
                }

                received.Clear();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ownsDataSource && _dataSource is not null)
            {
                await _dataSource.DisposeAsync();
            }
        }

        private NpgsqlDataSource DataSource
        {
            get
            {
                lock (_lock)
                {
                    if (_dataSource is null)
                    {
                        if (string.IsNullOrWhiteSpace(options.ConnectionString))
                        {
                            _dataSource = services.GetService(typeof(NpgsqlDataSource)) as NpgsqlDataSource
                                ?? throw new InvalidOperationException(
                                    "PostgreSQL change notifications need PostgreSqlChangeNotificationOptions.ConnectionString or an NpgsqlDataSource registered in DI.");
                        }
                        else
                        {
                            _dataSource = NpgsqlDataSource.Create(options.ConnectionString);
                            _ownsDataSource = true;
                        }
                    }

                    return _dataSource;
                }
            }
        }

        internal static string QuoteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }
}

namespace Microsoft.Extensions.DependencyInjection
{
    public static class DynamicEndpointsPostgreSqlExtensions
    {
        /// <summary>
        /// Tells the other instances about every change through PostgreSQL <c>LISTEN/NOTIFY</c>, so they reload right away – no
        /// extra infrastructure when the definitions already live in PostgreSQL. Uses the <see cref="NpgsqlDataSource"/> from DI unless
        /// <see cref="PostgreSqlChangeNotificationOptions.ConnectionString"/> is set. Every instance keeps one connection open for listening.
        /// Keep <c>RefreshInterval</c> as a fallback.
        /// </summary>
        public static IDynamicEndpointsBuilder UsePostgreSqlChangeNotifications(
            this IDynamicEndpointsBuilder builder,
            Action<PostgreSqlChangeNotificationOptions>? configure = null)
        {
            var options = new PostgreSqlChangeNotificationOptions();
            configure?.Invoke(options);
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Channel);
            if (System.Text.Encoding.UTF8.GetByteCount(options.Channel) > 63)
            {
                throw new ArgumentException("PostgreSQL channel names are limited to 63 bytes.", nameof(configure));
            }

            builder.Services.Replace(ServiceDescriptor.Singleton<IDynamicEndpointChangeNotifier>(sp => new PostgreSqlChangeNotifier(
                options, sp, sp.GetRequiredService<ILogger<PostgreSqlChangeNotifier>>())));
            return builder;
        }

        /// <summary>PostgreSQL <c>LISTEN/NOTIFY</c> change notifications using <paramref name="connectionString"/>.</summary>
        public static IDynamicEndpointsBuilder UsePostgreSqlChangeNotifications(
            this IDynamicEndpointsBuilder builder,
            string connectionString,
            string channel = "dynamic_endpoints") =>
            builder.UsePostgreSqlChangeNotifications(o =>
            {
                o.ConnectionString = connectionString;
                o.Channel = channel;
            });
    }
}
