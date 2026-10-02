using Microsoft.Extensions.Logging;

namespace DynamicEndpoints;

/// <summary>Writes audit entries to <see cref="ILogger"/> (category <c>DynamicEndpoints.Audit</c>) – the default sink.</summary>
public sealed class LoggerDynamicEndpointAuditSink(ILoggerFactory loggerFactory) : IDynamicEndpointAuditSink
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("DynamicEndpoints.Audit");

    public Task WriteAsync(DynamicEndpointAuditEntry entry, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            var changes = string.Join("; ", entry.Changes.Select(c =>
                $"{c.Path}: {c.Before?.ToJsonString() ?? "–"} → {c.After?.ToJsonString() ?? "–"}"));
            _logger.LogInformation(
                "Dynamic endpoint {AuditKind} {Method} {Route} ({EndpointId}, tenant {Tenant}, revision {Revision}) by {User}: {Changes}",
                entry.Kind, entry.Method, entry.Route, entry.EndpointId, entry.Tenant ?? "–", entry.Revision, entry.User ?? "–", changes);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Keeps the latest audit entries in memory – for tests and development. Queryable, so the admin API serves it under <c>/audit</c>.
/// Entries are lost on restart and not shared between instances.
/// </summary>
public sealed class InMemoryDynamicEndpointAuditLog(int capacity = 1000) : IDynamicEndpointAuditLog
{
    private readonly LinkedList<DynamicEndpointAuditEntry> _entries = new();
    private readonly Lock _lock = new();

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    /// <summary>All entries kept, oldest first.</summary>
    public IReadOnlyList<DynamicEndpointAuditEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToList();
            }
        }
    }

    public Task WriteAsync(DynamicEndpointAuditEntry entry, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _entries.AddLast(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveFirst();
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DynamicEndpointAuditEntry>> QueryAsync(DynamicEndpointAuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        IReadOnlyList<DynamicEndpointAuditEntry> result;
        lock (_lock)
        {
            result = _entries.Reverse()
                .Where(e => query.EndpointId is null || e.EndpointId == query.EndpointId)
                .Where(e => query.Tenant is null || DynamicEndpointsTenancyOptions.SameTenant(e.Tenant, query.Tenant))
                .Where(e => query.User is null || string.Equals(e.User, query.User, StringComparison.OrdinalIgnoreCase))
                .Where(e => query.From is null || e.Timestamp >= query.From)
                .Where(e => query.To is null || e.Timestamp <= query.To)
                .Take(Math.Max(query.Limit, 0))
                .ToList();
        }

        return Task.FromResult(result);
    }
}
