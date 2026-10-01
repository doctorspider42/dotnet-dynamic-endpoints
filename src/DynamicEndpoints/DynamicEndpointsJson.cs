using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicEndpoints;

/// <summary>Serializer settings used for persisting definitions and reading processor configuration.</summary>
public static class DynamicEndpointsJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = Create(strict: false);

    /// <summary>Same as <see cref="SerializerOptions"/>, but rejects unknown properties – used to catch typos in processor configuration.</summary>
    public static JsonSerializerOptions StrictSerializerOptions { get; } = Create(strict: true);

    private static JsonSerializerOptions Create(bool strict)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() },
        };
        if (strict)
        {
            options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        }

        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    internal static T DeepClone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions), SerializerOptions)!;
}
