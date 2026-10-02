using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>Per-request state shared by filters, validators and the processor of a dynamic endpoint.</summary>
internal sealed class DynamicRequestItems
{
    private static readonly object Key = new();

    public Dictionary<object, object?> Values { get; } = [];

    /// <summary>Parsed parameter values handed over by validators, keyed by parameter name.</summary>
    public Dictionary<string, object?> Parsed { get; } = new(StringComparer.Ordinal);

    public bool TryGetParsed<T>(string name, out T? value)
    {
        if (Parsed.TryGetValue(name, out var stored) && stored is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public static DynamicRequestItems For(HttpContext context)
    {
        if (context.Items.TryGetValue(Key, out var existing) && existing is DynamicRequestItems items)
        {
            return items;
        }

        items = new DynamicRequestItems();
        context.Items[Key] = items;
        return items;
    }
}
