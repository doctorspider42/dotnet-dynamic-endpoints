using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>One change of an endpoint definition, as recorded by the audit log (<c>AddAuditLog()</c>).</summary>
public sealed record DynamicEndpointAuditEntry
{
    public Guid Id { get; init; }

    public DateTimeOffset Timestamp { get; init; }

    public DynamicEndpointChangeKind Kind { get; init; }

    public Guid EndpointId { get; init; }

    public string? Tenant { get; init; }

    /// <summary>Who made the change – the user of the HTTP request (see <see cref="DynamicEndpointsAuditOptions.ResolveUser"/>); <c>null</c> outside requests (seeders, jobs) or for anonymous users.</summary>
    public string? User { get; init; }

    public string? Method { get; init; }

    public string? Route { get; init; }

    public string? Name { get; init; }

    /// <summary>Revision after the change (before it, for deletions).</summary>
    public int Revision { get; init; }

    /// <summary>What changed, property by property (<c>parameters[1].maxLength</c>). Revision and timestamps are left out.</summary>
    public IReadOnlyList<DynamicEndpointAuditChange> Changes { get; init; } = [];

    /// <summary>The definition before the change (when <see cref="DynamicEndpointsAuditOptions.IncludeDefinitions"/>).</summary>
    public DynamicEndpointDefinition? Previous { get; init; }

    /// <summary>The definition after the change (when <see cref="DynamicEndpointsAuditOptions.IncludeDefinitions"/>).</summary>
    public DynamicEndpointDefinition? Definition { get; init; }

    /// <summary>Trace id of the request that made the change.</summary>
    public string? TraceId { get; init; }

    /// <summary><see cref="DynamicEndpointsOptions.InstanceId"/> of the instance that made the change.</summary>
    public string? InstanceId { get; init; }
}

/// <summary>A changed property: <c>null</c> on one side means it was absent.</summary>
public sealed record DynamicEndpointAuditChange(string Path, JsonNode? Before, JsonNode? After);

/// <summary>Filter for <see cref="IDynamicEndpointAuditLog.QueryAsync"/>; entries come newest first.</summary>
public sealed record DynamicEndpointAuditQuery
{
    public Guid? EndpointId { get; init; }

    /// <summary>Entries of this tenant only.</summary>
    public string? Tenant { get; init; }

    public string? User { get; init; }

    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    /// <summary>Maximum number of entries. Default 100.</summary>
    public int Limit { get; init; } = 100;
}

/// <summary>Where audit entries go – a log, a database table, a message bus. Register with <c>AddAuditLog(a =&gt; a.To&lt;T&gt;())</c>.</summary>
public interface IDynamicEndpointAuditSink
{
    Task WriteAsync(DynamicEndpointAuditEntry entry, CancellationToken cancellationToken);
}

/// <summary>An audit sink that can be queried – exposed by the admin API under <c>/audit</c>.</summary>
public interface IDynamicEndpointAuditLog : IDynamicEndpointAuditSink
{
    Task<IReadOnlyList<DynamicEndpointAuditEntry>> QueryAsync(DynamicEndpointAuditQuery query, CancellationToken cancellationToken);
}

public sealed class DynamicEndpointsAuditOptions
{
    /// <summary>
    /// Name of the user who made a change. Default: <c>Identity.Name</c>, else the <c>NameIdentifier</c> or <c>sub</c> claim of an
    /// authenticated user.
    /// </summary>
    public Func<HttpContext, string?> ResolveUser { get; set; } = DefaultUser;

    /// <summary>Keep the complete definitions before and after each change in the entry (default), not just the changed properties.</summary>
    public bool IncludeDefinitions { get; set; } = true;

    private static string? DefaultUser(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true
            ? context.User.Identity.Name ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value
            : null;
}
