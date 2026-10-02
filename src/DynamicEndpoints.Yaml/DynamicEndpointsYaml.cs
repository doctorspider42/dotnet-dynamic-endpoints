using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DynamicEndpoints.Yaml;

/// <summary>
/// Converts between YAML and the JSON model the library works with. Plain scalars follow the YAML 1.2 core schema (<c>null</c>,
/// booleans, integers, floats); quoted and block scalars are strings. Only the first document of a stream is read.
/// </summary>
public static partial class DynamicEndpointsYaml
{
    private const int MaxDepth = 256;

    [GeneratedRegex("^[-+]?[0-9]+$")]
    private static partial Regex Integer();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex Float();

    /// <exception cref="FormatException">The text is not valid YAML.</exception>
    public static JsonNode? Parse(string yaml)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlException ex)
        {
            throw new FormatException($"Invalid YAML: {ex.Message}", ex);
        }

        return stream.Documents.Count == 0 ? null : ToJson(stream.Documents[0].RootNode, 0);
    }

    /// <summary>Block-style YAML with two-space indentation and <c>\n</c> line endings – stable for the same content.</summary>
    public static string Write(JsonNode? node)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var emitter = new Emitter(writer, EmitterSettings.Default.WithBestIndent(2).WithBestWidth(int.MaxValue).WithIndentedSequences());
        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart());
        Emit(emitter, node);
        emitter.Emit(new DocumentEnd(isImplicit: true));
        emitter.Emit(new StreamEnd());
        return writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static JsonNode? ToJson(YamlNode node, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("The YAML document is nested too deeply.");
        }

        switch (node)
        {
            case YamlMappingNode mapping:
                var obj = new JsonObject();
                foreach (var (key, value) in mapping.Children)
                {
                    var name = key is YamlScalarNode scalarKey ? scalarKey.Value ?? string.Empty
                        : throw new FormatException($"Only scalar mapping keys are supported (line {key.Start.Line}).");
                    obj[name] = ToJson(value, depth + 1);
                }

                return obj;
            case YamlSequenceNode sequence:
                return new JsonArray(sequence.Children.Select(c => ToJson(c, depth + 1)).ToArray());
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var value = scalar.Value ?? string.Empty;
        if (scalar.Style != ScalarStyle.Plain || (!scalar.Tag.IsEmpty && scalar.Tag.Value is "tag:yaml.org,2002:str" or "!!str"))
        {
            return JsonValue.Create(value);
        }

        return Resolve(value);
    }

    /// <summary>What a plain scalar means: <c>null</c> for null, otherwise a JSON value.</summary>
    private static JsonNode? Resolve(string value)
    {
        switch (value)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (Integer().IsMatch(value))
        {
            if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer))
            {
                return JsonValue.Create(integer);
            }

            if (decimal.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big))
            {
                return JsonValue.Create(big);
            }
        }

        if (value.StartsWith("0x", StringComparison.Ordinal) &&
            long.TryParse(value[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
        {
            return JsonValue.Create(hex);
        }

        if (Float().IsMatch(value) && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            // Keep the text as written ("1.50" stays 1.50) – JSON numbers are arbitrary precision.
            return JsonNode.Parse(number.ToString(CultureInfo.InvariantCulture));
        }

        return JsonValue.Create(value);
    }

    private static bool IsPlainString(string value) =>
        value.Length > 0 && Resolve(value) is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == value;

    private static void Emit(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                emitter.Emit(new MappingStart(AnchorName.Empty, TagName.Empty, isImplicit: true, obj.Count == 0 ? MappingStyle.Flow : MappingStyle.Block));
                foreach (var (key, value) in obj)
                {
                    EmitString(emitter, key);
                    Emit(emitter, value);
                }

                emitter.Emit(new MappingEnd());
                break;
            case JsonArray array:
                emitter.Emit(new SequenceStart(AnchorName.Empty, TagName.Empty, isImplicit: true, array.Count == 0 ? SequenceStyle.Flow : SequenceStyle.Block));
                foreach (var item in array)
                {
                    Emit(emitter, item);
                }

                emitter.Emit(new SequenceEnd());
                break;
            case null:
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, "null", ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                EmitString(emitter, value.GetValue<string>());
                break;
            default:
                // Numbers and booleans: their JSON text is a valid plain YAML scalar of the same type.
                emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, node.ToJsonString(), ScalarStyle.Plain, isPlainImplicit: true, isQuotedImplicit: false));
                break;
        }
    }

    private static void EmitString(IEmitter emitter, string value)
    {
        var style = value.Contains('\n') ? ScalarStyle.Literal
            : IsPlainString(value) ? ScalarStyle.Any
            : ScalarStyle.DoubleQuoted;
        emitter.Emit(new Scalar(AnchorName.Empty, TagName.Empty, value, style, isPlainImplicit: style == ScalarStyle.Any, isQuotedImplicit: true));
    }
}

/// <summary>The YAML format for the admin API's export, import and OpenAPI import.</summary>
public sealed class YamlDynamicEndpointsTextFormat : IDynamicEndpointsTextFormat
{
    public string Name => "yaml";

    public IReadOnlyList<string> MediaTypes { get; } = ["application/yaml", "application/x-yaml", "text/yaml", "text/x-yaml"];

    public JsonNode? Parse(string text) => DynamicEndpointsYaml.Parse(text);

    public string Write(JsonNode node) => DynamicEndpointsYaml.Write(node);
}
