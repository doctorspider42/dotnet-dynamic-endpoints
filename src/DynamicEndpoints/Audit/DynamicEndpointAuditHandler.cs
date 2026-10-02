using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Audit;

/// <summary>Turns local changes into audit entries and hands them to every sink.</summary>
internal sealed class DynamicEndpointAuditHandler(
    IEnumerable<IDynamicEndpointAuditSink> sinks,
    IOptions<DynamicEndpointsAuditOptions> auditOptions,
    IOptions<DynamicEndpointsOptions> options,
    TimeProvider time,
    ILogger<DynamicEndpointAuditHandler> logger,
    IHttpContextAccessor? httpContextAccessor = null) : IDynamicEndpointChangeHandler
{
    public async Task OnChangedAsync(DynamicEndpointChangedEvent change, CancellationToken cancellationToken)
    {
        // Remote changes are raised on every other instance – the instance that made the change audits it, once.
        if (change.Origin != DynamicEndpointChangeOrigin.Local)
        {
            return;
        }

        var entry = Create(change);
        foreach (var sink in sinks)
        {
            try
            {
                await sink.WriteAsync(entry, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit sink {Sink} failed for {Kind} of dynamic endpoint {Id}.", sink.GetType().Name, change.Kind, change.Id);
            }
        }
    }

    private DynamicEndpointAuditEntry Create(DynamicEndpointChangedEvent change)
    {
        var current = change.Definition ?? change.Previous;
        var http = httpContextAccessor?.HttpContext;
        var include = auditOptions.Value.IncludeDefinitions;
        return new DynamicEndpointAuditEntry
        {
            Id = Guid.CreateVersion7(),
            Timestamp = time.GetUtcNow(),
            Kind = change.Kind,
            EndpointId = change.Id,
            Tenant = current?.Tenant,
            User = http is null ? null : auditOptions.Value.ResolveUser(http),
            Method = current?.Method,
            Route = current?.Route,
            Name = current?.Name,
            Revision = current?.Revision ?? 0,
            Changes = DefinitionDiff.Compare(change.Previous, change.Definition),
            Previous = include ? change.Previous : null,
            Definition = include ? change.Definition : null,
            TraceId = Activity.Current?.Id ?? http?.TraceIdentifier,
            InstanceId = options.Value.InstanceId,
        };
    }
}

/// <summary>Property-by-property differences of two definitions, as JSON paths (<c>parameters[0].maxLength</c>).</summary>
internal static class DefinitionDiff
{
    private static readonly string[] Ignored = ["revision", "createdAt", "updatedAt"];

    public static IReadOnlyList<DynamicEndpointAuditChange> Compare(DynamicEndpointDefinition? before, DynamicEndpointDefinition? after)
    {
        var changes = new List<DynamicEndpointAuditChange>();
        Compare(string.Empty, ToNode(before), ToNode(after), changes);
        return changes;
    }

    private static JsonObject ToNode(DynamicEndpointDefinition? definition)
    {
        if (definition is null)
        {
            return [];
        }

        var node = JsonSerializer.SerializeToNode(definition, DynamicEndpointsJson.SerializerOptions)!.AsObject();
        foreach (var name in Ignored)
        {
            node.Remove(name);
        }

        return node;
    }

    private static void Compare(string path, JsonNode? before, JsonNode? after, List<DynamicEndpointAuditChange> changes)
    {
        switch (before, after)
        {
            case (JsonObject a, JsonObject b):
                foreach (var name in a.Select(p => p.Key).Union(b.Select(p => p.Key)))
                {
                    Compare(path.Length == 0 ? name : $"{path}.{name}", a[name], b[name], changes);
                }

                break;
            case (JsonArray a, JsonArray b):
                for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
                {
                    Compare($"{path}[{i}]", i < a.Count ? a[i] : null, i < b.Count ? b[i] : null, changes);
                }

                break;
            default:
                if (!JsonNode.DeepEquals(before, after))
                {
                    changes.Add(new DynamicEndpointAuditChange(path, before?.DeepClone(), after?.DeepClone()));
                }

                break;
        }
    }
}
