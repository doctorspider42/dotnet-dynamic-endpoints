using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints;

/// <summary>
/// A text format for exported definitions and imported OpenAPI documents. JSON is built in; YAML comes with the
/// <c>DynamicEndpoints.Yaml</c> package (<c>AddYamlFormat()</c>). The admin API picks the format by <c>Content-Type</c> when reading
/// and by <c>?format=</c> or <c>Accept</c> when writing.
/// </summary>
public interface IDynamicEndpointsTextFormat
{
    /// <summary>Short name used in <c>?format=</c>, e.g. <c>json</c> or <c>yaml</c>.</summary>
    string Name { get; }

    /// <summary>Media types of the format; the first one is used for responses.</summary>
    IReadOnlyList<string> MediaTypes { get; }

    /// <exception cref="FormatException">The text is not valid in this format.</exception>
    JsonNode? Parse(string text);

    /// <summary>Writes <paramref name="node"/> in a stable form: same content, same text.</summary>
    string Write(JsonNode node);
}

/// <summary>The built-in JSON format: indented, <c>\n</c> line endings, a trailing newline – friendly to diffs.</summary>
public sealed class JsonDynamicEndpointsTextFormat : IDynamicEndpointsTextFormat
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string Name => "json";

    public IReadOnlyList<string> MediaTypes { get; } = ["application/json", "text/json"];

    public JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid JSON: {ex.Message}", ex);
        }
    }

    public string Write(JsonNode node) => new StringBuilder(node.ToJsonString(Indented)).Append('\n').ToString();
}

internal static class DynamicEndpointsTextFormats
{
    /// <summary>The format of a request body, by its content type; JSON when unknown.</summary>
    public static IDynamicEndpointsTextFormat ForContentType(IEnumerable<IDynamicEndpointsTextFormat> formats, string? contentType)
    {
        var list = formats.ToList();
        var media = contentType?.Split(';')[0].Trim();
        return list.FirstOrDefault(f => f.MediaTypes.Contains(media, StringComparer.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(f => f.Name == "json")
            ?? new JsonDynamicEndpointsTextFormat();
    }

    /// <summary>The format of a response: <c>?format=</c> first, then <c>Accept</c>, JSON otherwise. <c>null</c> for an unknown name.</summary>
    public static IDynamicEndpointsTextFormat? ForResponse(IEnumerable<IDynamicEndpointsTextFormat> formats, string? name, string? accept)
    {
        var list = formats.ToList();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return list.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) ||
                (f.Name == "yaml" && string.Equals(name, "yml", StringComparison.OrdinalIgnoreCase)));
        }

        foreach (var media in (accept ?? string.Empty).Split(',').Select(a => a.Split(';')[0].Trim()))
        {
            if (list.FirstOrDefault(f => f.MediaTypes.Contains(media, StringComparer.OrdinalIgnoreCase)) is { } format)
            {
                return format;
            }
        }

        return list.FirstOrDefault(f => f.Name == "json") ?? new JsonDynamicEndpointsTextFormat();
    }
}
