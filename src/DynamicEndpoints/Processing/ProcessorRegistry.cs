using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.Processing;

internal sealed class ProcessorRegistryOptions
{
    public List<DynamicProcessorDescriptor> Processors { get; } = [];
}

internal sealed class ProcessorRegistry
{
    private readonly Dictionary<string, DynamicProcessorDescriptor> _processors;

    public ProcessorRegistry(IOptions<ProcessorRegistryOptions> options)
    {
        _processors = new Dictionary<string, DynamicProcessorDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in options.Value.Processors)
        {
            if (!_processors.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException($"Dynamic endpoint processor '{descriptor.Name}' is registered more than once.");
            }
        }

        All = _processors.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<DynamicProcessorDescriptor> All { get; }

    public bool TryGet(string name, out DynamicProcessorDescriptor descriptor) =>
        _processors.TryGetValue(name, out descriptor!);

    public static DynamicProcessorDescriptor Describe(Type type, string? name, string? description)
    {
        var attribute = type.GetCustomAttributes(typeof(DynamicProcessorAttribute), false)
            .OfType<DynamicProcessorAttribute>()
            .FirstOrDefault();

        name ??= attribute?.Name ?? RegistryNames.Derive(type, "Processor");
        description ??= attribute?.Description;
        return new DynamicProcessorDescriptor(name, description, RegistryNames.ParseExample(attribute?.ConfigurationExample, name));
    }
}

internal static class RegistryNames
{
    // ("OrderLookupProcessor", "Processor") -> "order-lookup"
    public static string Derive(Type type, string suffix)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        if (tick > 0)
        {
            name = name[..tick];
        }

        if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
        {
            name = name[..^suffix.Length];
        }

        var chars = new List<char>(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                chars.Add('-');
            }

            chars.Add(char.ToLowerInvariant(name[i]));
        }

        return new string(chars.ToArray());
    }

    public static JsonObject? ParseExample(string? json, string name) => json is null
        ? null
        : JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"Configuration example of '{name}' must be a JSON object.");
}

internal sealed class DelegateProcessor(Func<DynamicRequest, Task<IResult>> handler) : IDynamicEndpointProcessor
{
    public Task<IResult> ProcessAsync(DynamicRequest request) => handler(request);
}
