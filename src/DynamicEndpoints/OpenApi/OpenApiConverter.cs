using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicEndpoints.Snippets;
using DynamicEndpoints.Validation.Engine;

namespace DynamicEndpoints.OpenApi;

/// <summary>
/// Maps the operations of an OpenAPI 3.x document to definition skeletons: routes, methods, parameters with types and
/// constraints, body properties, response schemas and examples. Whatever has no counterpart is reported, never guessed.
/// </summary>
internal static partial class OpenApiConverter
{
    private static readonly string[] Methods = ["get", "post", "put", "patch", "delete", "head", "options", "trace"];

    private static readonly HashSet<string> SchemaAnnotations = ["$schema", "$comment", "title", "description", "default", "examples", "deprecated", "readOnly", "writeOnly"];

    private static readonly HashSet<string> SchemaKeywords =
    [
        "type", "enum", "const", "minLength", "maxLength", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "properties", "required", "additionalProperties", "minProperties", "maxProperties", "items", "minItems", "maxItems", "uniqueItems",
    ];

    // Formats that only describe a size or a UI hint – dropped without a note.
    private static readonly HashSet<string> BenignFormats = ["int32", "int64", "float", "double", "password", "decimal"];

    private static readonly HashSet<string> IgnoredHeaders = new(StringComparer.OrdinalIgnoreCase) { "Accept", "Content-Type", "Authorization" };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterName();

    public const string ProcessorExtension = "x-dynamic-endpoints-processor";

    public const string ProcessorConfigExtension = "x-dynamic-endpoints-processor-config";

    /// <summary>Name of the built-in processor mock mode configures.</summary>
    public const string MockProcessor = "response";

    public sealed record Operation(
        string Method,
        string Path,
        string? OperationId,
        DynamicEndpointDefinition? Definition,
        List<string> Unmapped,
        OpenApiProcessorSource ProcessorSource = OpenApiProcessorSource.Default,
        string? ProcessorReason = null);

    /// <param name="Operations">The selected operations.</param>
    /// <param name="Warnings">Document-level remarks.</param>
    /// <param name="DocumentId">Identity of the document in <see cref="DynamicEndpointOrigin.Document"/>.</param>
    /// <param name="OperationKeys">Origins (<see cref="DynamicEndpointOrigin.Operation"/>) of every operation in the document – selected or not.</param>
    /// <param name="Tags">Tags of the document and its operations.</param>
    public sealed record Conversion(List<Operation> Operations, List<string> Warnings, string? DocumentId, HashSet<string> OperationKeys, List<string> Tags);

    /// <summary><see cref="DynamicEndpointOrigin.Operation"/> of an operation: its operationId, or method and path.</summary>
    public static string OperationKey(string method, string path, string? operationId) => operationId ?? $"{method} {path}";

    /// <exception cref="FormatException">Not an OpenAPI 3.x document.</exception>
    public static Conversion Convert(JsonNode? document, OpenApiImportOptions options)
    {
        if (document is not JsonObject root)
        {
            throw new FormatException("An OpenAPI document must be a JSON object.");
        }

        if (root["swagger"] is not null)
        {
            throw new FormatException("Swagger 2.0 documents are not supported – convert them to OpenAPI 3 first.");
        }

        var version = Text(root["openapi"]);
        if (version is null || !version.StartsWith("3.", StringComparison.Ordinal))
        {
            throw new FormatException("The document is not OpenAPI 3.x (missing or unsupported 'openapi' version).");
        }

        var warnings = new List<string>();
        var operations = new List<Operation>();
        var resolver = new Resolver(root);
        var documentSecurity = root["security"] as JsonArray;
        var documentId = Clean(options.DocumentId) ?? Text(root["info"]?["title"])?.Trim();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var tags = (root["tags"] as JsonArray ?? []).Select(t => Text(t?["name"])).OfType<string>().ToList();
        var documentProcessor = Extension(root, "the document", warnings);

        if (root["paths"] is not JsonObject paths)
        {
            warnings.Add("The document has no paths.");
            return new Conversion(operations, warnings, documentId, keys, tags);
        }

        foreach (var (path, pathNode) in paths)
        {
            var notes = new List<string>();
            if (resolver.Resolve(pathNode, notes) is not JsonObject pathItem)
            {
                continue;
            }

            var pathProcessor = Extension(pathItem, $"path '{path}'", notes);
            foreach (var method in Methods)
            {
                if (pathItem[method] is not JsonObject operation)
                {
                    continue;
                }

                var upper = method.ToUpperInvariant();
                var operationId = Text(operation["operationId"]);
                var operationTags = Strings(operation["tags"]).ToList();
                keys.Add(OperationKey(upper, path, operationId));
                tags.AddRange(operationTags);
                if (options.Tags is { Count: > 0 } only && !operationTags.Any(t => only.Contains(t, StringComparer.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (method is "head" or "options" or "trace")
                {
                    operations.Add(new Operation(upper, path, operationId, null, [$"{upper} operations are not supported by dynamic endpoints."]));
                    continue;
                }

                var unmapped = new List<string>(notes);
                var definition = ConvertOperation(upper, path, operation, pathItem, documentSecurity, resolver, options, unmapped);
                var choice = ChooseProcessor(operation, operationTags, pathProcessor, documentProcessor, options, resolver, unmapped);
                definition = definition with
                {
                    Processor = choice.Processor,
                    ProcessorConfig = choice.Config,
                    Origin = new DynamicEndpointOrigin { Kind = DynamicEndpointOrigin.OpenApi, Document = documentId, Operation = OperationKey(upper, path, operationId) },
                };
                operations.Add(new Operation(upper, path, operationId, definition, unmapped.Distinct().ToList(), choice.Source, choice.Reason));
            }
        }

        return new Conversion(operations, warnings, documentId, keys, tags.Distinct(StringComparer.Ordinal).ToList());
    }

    private sealed record ProcessorChoice(string? Processor, JsonObject? Config, OpenApiProcessorSource Source, string? Reason);

    /// <summary>
    /// Operation extension &gt; path extension &gt; tag mapping &gt; document extension &gt; <c>Processor</c> option &gt; default. In mock
    /// mode the import options (tag mapping, <c>Processor</c>, default) give way to the <c>response</c> processor; extensions still win.
    /// </summary>
    private static ProcessorChoice ChooseProcessor(
        JsonObject operation,
        List<string> operationTags,
        ProcessorChoice? pathProcessor,
        ProcessorChoice? documentProcessor,
        OpenApiImportOptions options,
        Resolver resolver,
        List<string> unmapped)
    {
        if (Extension(operation, "the operation", unmapped) is { } own)
        {
            return own;
        }

        if (pathProcessor is not null)
        {
            return pathProcessor;
        }

        if (!options.Mock && options.ProcessorsByTag is { Count: > 0 } byTag)
        {
            foreach (var tag in operationTags)
            {
                var mapping = byTag.FirstOrDefault(m => string.Equals(m.Key, tag, StringComparison.OrdinalIgnoreCase));
                if (mapping.Value is { } map && Clean(map.Processor) is { } processor)
                {
                    return new ProcessorChoice(processor, map.ProcessorConfig?.DeepClone() as JsonObject, OpenApiProcessorSource.Tag, $"tag '{mapping.Key}'");
                }
            }
        }

        if (documentProcessor is not null)
        {
            return documentProcessor;
        }

        if (options.Mock)
        {
            var (config, reason) = MockConfig(operation, resolver, unmapped);
            return new ProcessorChoice(MockProcessor, config, OpenApiProcessorSource.Mock, reason);
        }

        return Clean(options.Processor) is { } option
            ? new ProcessorChoice(option, options.ProcessorConfig?.DeepClone() as JsonObject, OpenApiProcessorSource.Option, "the processor option")
            : new ProcessorChoice(null, null, OpenApiProcessorSource.Default, "DefaultProcessor");
    }

    /// <summary>The processor named by <c>x-dynamic-endpoints-processor</c> (and its <c>-config</c>) on an operation, path item or document.</summary>
    private static ProcessorChoice? Extension(JsonObject owner, string where, List<string> notes)
    {
        var name = owner[ProcessorExtension];
        var config = owner[ProcessorConfigExtension];
        if (name is null)
        {
            if (config is not null)
            {
                notes.Add($"'{ProcessorConfigExtension}' of {where} without '{ProcessorExtension}' – ignored.");
            }

            return null;
        }

        if (Text(name)?.Trim() is not { Length: > 0 } processor)
        {
            notes.Add($"'{ProcessorExtension}' of {where} is not a processor name – ignored.");
            return null;
        }

        if (config is not null and not JsonObject)
        {
            notes.Add($"'{ProcessorConfigExtension}' of {where} is not an object – ignored.");
        }

        var source = where switch
        {
            "the operation" => OpenApiProcessorSource.OperationExtension,
            "the document" => OpenApiProcessorSource.DocumentExtension,
            _ => OpenApiProcessorSource.PathExtension,
        };
        return new ProcessorChoice(processor, (config as JsonObject)?.DeepClone() as JsonObject, source, $"{ProcessorExtension} of {where}");
    }

    /// <summary>
    /// Configuration of the <c>response</c> processor answering like the operation's first 2xx response: its status code, content
    /// type and example – or one generated from its schema.
    /// </summary>
    private static (JsonObject Config, string Reason) MockConfig(JsonObject operation, Resolver resolver, List<string> unmapped)
    {
        var responses = operation["responses"] as JsonObject ?? [];
        var documented = responses
            .Where(r => r.Key.Length == 3 && r.Key[0] is >= '1' and <= '5' && (int.TryParse(r.Key, out _) || r.Key[1..].Equals("XX", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chosen = documented.FirstOrDefault(r => r.Key[0] == '2');
        if (chosen.Key is null && documented.Count > 0)
        {
            chosen = documented[0];
            unmapped.Add($"mock: no 2xx response – it answers with the documented {chosen.Key}.");
        }

        if (chosen.Key is null)
        {
            unmapped.Add("mock: no documented status code – it answers 204 No Content.");
            return (new JsonObject { ["statusCode"] = 204 }, "mock: 204, no response documented");
        }

        var status = int.TryParse(chosen.Key, out var code) ? code : (chosen.Key[0] - '0') * 100;    // "2XX" answers 200
        var config = new JsonObject { ["statusCode"] = status };
        if (resolver.Resolve(chosen.Value, unmapped) is not JsonObject response || response["content"] is not JsonObject { Count: > 0 } content)
        {
            return (config, $"mock: {status} without a body");
        }

        var (mediaType, media) = content
            .Select(c => (Type: c.Key.Split(';')[0].Trim().ToLowerInvariant(), Media: c.Value as JsonObject))
            .OrderBy(c => IsJson(c.Type) ? 0 : c.Type.StartsWith("text/", StringComparison.Ordinal) ? 1 : 2)
            .First();
        var example = media?["example"]?.DeepClone()
            ?? (media?["examples"] as JsonObject)?.Select(e => resolver.Resolve(e.Value, unmapped)?["value"]).FirstOrDefault(v => v is not null)?.DeepClone();
        var generated = example is null;
        var json = IsJson(mediaType);
        if (generated && media?["schema"] is { } schemaNode && (json || mediaType.StartsWith("text/", StringComparison.Ordinal)))
        {
            example = ExampleValues.FromSchema(resolver.Inline(schemaNode, unmapped), 0);
        }

        if (json && example is not null)
        {
            config["body"] = example;
            if (mediaType != "application/json")
            {
                config["contentType"] = mediaType;
            }
        }
        else if (mediaType.StartsWith("text/", StringComparison.Ordinal) && example is not null)
        {
            config["text"] = example is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : example.ToJsonString();
            config["contentType"] = mediaType == "text/plain" ? "text/plain; charset=utf-8" : mediaType;
        }
        else
        {
            unmapped.Add($"mock: no example of the '{mediaType}' response – it answers {status} without a body.");
            return (config, $"mock: {status} without a body");
        }

        return (config, $"mock: {status} with {(generated ? "a body generated from the schema" : "the documented example")}");
    }

    private static bool IsJson(string mediaType) => mediaType is "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DynamicEndpointDefinition ConvertOperation(
        string method,
        string path,
        JsonObject operation,
        JsonObject pathItem,
        JsonArray? documentSecurity,
        Resolver resolver,
        OpenApiImportOptions options,
        List<string> unmapped)
    {
        var parameters = new List<ParameterDefinition>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var route = Route(options.RoutePrefix, path);

        // Operation-level parameters override path-level ones with the same name and location.
        var declared = new Dictionary<(string, string), JsonObject>();
        foreach (var source in new[] { pathItem["parameters"], operation["parameters"] })
        {
            foreach (var item in source as JsonArray ?? [])
            {
                if (resolver.Resolve(item, unmapped) is JsonObject parameter && Text(parameter["name"]) is { } name && Text(parameter["in"]) is { } location)
                {
                    declared[(location, name)] = parameter;
                }
            }
        }

        foreach (var ((location, name), parameter) in declared)
        {
            var source = location switch
            {
                "path" => ParameterSource.Route,
                "query" => ParameterSource.Query,
                "header" => ParameterSource.Header,
                _ => (ParameterSource?)null,
            };
            if (source is null)
            {
                unmapped.Add($"{location} parameter '{name}' (only path, query and header parameters are supported).");
                continue;
            }

            if (source == ParameterSource.Header && IgnoredHeaders.Contains(name))
            {
                unmapped.Add($"header parameter '{name}' (handled by HTTP itself).");
                continue;
            }

            if (Text(parameter["style"]) is { } style && style is not ("simple" or "form") || parameter["content"] is not null)
            {
                unmapped.Add($"parameter '{name}': serialization style/content – bound as a plain value.");
            }

            var p = new ParameterDefinition
            {
                Name = UniqueName(name, names),
                Source = source.Value,
                Required = source == ParameterSource.Route || Bool(parameter["required"]),
                Description = Text(parameter["description"]),
            };
            p = p with { SourceName = p.Name == name ? null : name };
            var schema = resolver.Resolve(parameter["schema"], unmapped) as JsonObject ?? new JsonObject();
            p = MapSchema(p, schema, resolver, $"parameter '{name}'", unmapped);
            p = p with { Example = ExampleOf(parameter) ?? p.Example };
            parameters.Add(Finish(p, $"parameter '{name}'", unmapped));
        }

        var bodyAllowed = method is "POST" or "PUT" or "PATCH";
        if (resolver.Resolve(operation["requestBody"], unmapped) is JsonObject requestBody && requestBody["content"] is JsonObject content)
        {
            var (mediaType, media) = PickMedia(content);
            if (!bodyAllowed)
            {
                unmapped.Add($"request body of a {method} operation.");
            }
            else if (media is null)
            {
                unmapped.Add($"request body ({string.Join(", ", content.Select(c => c.Key))}) – only JSON and form bodies are supported.");
            }
            else
            {
                var form = mediaType is "multipart/form-data" or "application/x-www-form-urlencoded";
                var schema = Merge(resolver.Resolve(media["schema"], unmapped) as JsonObject ?? new JsonObject(), resolver, unmapped);
                if (TypeOf(schema) != "object")
                {
                    unmapped.Add($"request body schema of type '{TypeOf(schema) ?? "unknown"}' – only object bodies map to parameters.");
                }
                else
                {
                    var required = Strings(schema["required"]).ToHashSet(StringComparer.Ordinal);
                    foreach (var (property, propertyNode) in schema["properties"] as JsonObject ?? [])
                    {
                        var propertySchema = resolver.Resolve(propertyNode, unmapped) as JsonObject ?? new JsonObject();
                        var p = new ParameterDefinition
                        {
                            Name = UniqueName(property, names),
                            Source = form ? ParameterSource.Form : ParameterSource.Body,
                            Required = required.Contains(property),
                            Description = Text(propertySchema["description"]),
                        };
                        p = p with { SourceName = p.Name == property ? null : property };
                        p = MapSchema(p, propertySchema, resolver, $"body property '{property}'", unmapped);
                        if (form && DynamicEndpointMetadata.IsFile(p) &&
                            Text((media["encoding"] as JsonObject)?[property]?["contentType"]) is { } contentTypes)
                        {
                            p = p with { AllowedContentTypes = contentTypes.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList() };
                        }

                        parameters.Add(Finish(p, $"body property '{property}'", unmapped));
                    }

                    if (schema["additionalProperties"] is JsonObject || IsTrue(schema["additionalProperties"]))
                    {
                        unmapped.Add("request body 'additionalProperties' – only the declared properties are bound.");
                    }
                }
            }
        }

        var (responseSchema, responseExample) = Response(operation, resolver, unmapped);
        var security = operation["security"] as JsonArray ?? documentSecurity;
        var requiresAuthorization = security is { Count: > 0 } && security.All(r => r is JsonObject { Count: > 0 });
        if (requiresAuthorization)
        {
            var schemes = security!.SelectMany(r => r!.AsObject().Select(s => s.Key)).Distinct();
            unmapped.Add($"security ({string.Join(", ", schemes)}) – mapped to 'requireAuthorization'; set a policy if needed.");
        }

        var callbacks = operation["callbacks"] is JsonObject { Count: > 0 };
        if (callbacks)
        {
            unmapped.Add("callbacks.");
        }

        return new DynamicEndpointDefinition
        {
            Method = method,
            Route = route,
            Name = Text(operation["summary"]) ?? Text(operation["operationId"]),
            Description = Text(operation["description"]),
            Group = options.Group ?? Strings(operation["tags"]).FirstOrDefault(),
            Parameters = parameters,
            ResponseSchema = responseSchema,
            ResponseExample = responseExample,
            RequireAuthorization = requiresAuthorization,
            AllowAnonymous = security is { Count: 0 } || (security?.Any(r => r is JsonObject { Count: 0 }) ?? false),
            Enabled = options.Enabled,
        };
    }

    private static ParameterDefinition MapSchema(ParameterDefinition p, JsonObject schema, Resolver resolver, string what, List<string> unmapped)
    {
        schema = Merge(schema, resolver, unmapped);
        var type = TypeOf(schema);
        p = p with
        {
            Default = schema["default"]?.DeepClone(),
            Example = schema["example"]?.DeepClone() ?? (schema["examples"] as JsonArray is [var first, ..] ? first?.DeepClone() : null),
            Description = p.Description ?? Text(schema["description"]),
        };

        switch (type)
        {
            case "array":
                var items = Merge(resolver.Resolve(schema["items"], unmapped) as JsonObject ?? new JsonObject(), resolver, unmapped);
                var itemType = TypeOf(items);
                p = p with { Type = ParameterType.Array, MinItems = Int(schema["minItems"]), MaxItems = Int(schema["maxItems"]) };
                if (Bool(schema["uniqueItems"]))
                {
                    unmapped.Add($"{what}: 'uniqueItems'.");
                }

                if (itemType is "object" or "array")
                {
                    if (p.Source == ParameterSource.Body)
                    {
                        return p with { Schema = Sanitize(schema, resolver, what, unmapped, ["minItems", "maxItems"]) };
                    }

                    unmapped.Add($"{what}: arrays of {itemType}s – items are bound as strings.");
                    return p with { ItemType = ParameterType.String };
                }

                var scalar = MapScalar(p with { Type = ParameterType.String }, items, itemType, what, unmapped);
                return scalar with
                {
                    Type = ParameterType.Array,
                    ItemType = scalar.Type,
                    Default = p.Default,
                    Example = p.Example,
                    MinItems = p.MinItems,
                    MaxItems = p.MaxItems,
                };
            case "object":
                if (p.Source != ParameterSource.Body)
                {
                    unmapped.Add($"{what}: object values outside a JSON body – bound as a string.");
                    return p with { Type = ParameterType.String };
                }

                var sanitized = Sanitize(schema, resolver, what, unmapped, []);
                return p with { Type = ParameterType.Object, Schema = sanitized.Count > 1 ? sanitized : null };
            default:
                return MapScalar(p, schema, type, what, unmapped);
        }
    }

    private static ParameterDefinition MapScalar(ParameterDefinition p, JsonObject schema, string? type, string what, List<string> unmapped)
    {
        var format = Text(schema["format"]);
        if (schema["enum"] is JsonArray { Count: > 0 } values)
        {
            p = p with { AllowedValues = values.Where(v => v is not null).Select(v => v!.DeepClone()).ToList() };
        }

        if (schema["multipleOf"] is not null)
        {
            unmapped.Add($"{what}: 'multipleOf'.");
        }

        switch (type)
        {
            case "integer":
            case "number":
                var integer = type == "integer";
                var minimum = ExampleValues.Dec(schema["minimum"]);
                var maximum = ExampleValues.Dec(schema["maximum"]);
                // OpenAPI 3.0: boolean exclusive flags; 3.1: numeric bounds.
                if (schema["exclusiveMinimum"] is { } exMin)
                {
                    var bound = ExampleValues.Dec(exMin) ?? (IsTrue(exMin) ? minimum : null);
                    if (bound is { } b)
                    {
                        minimum = integer ? Math.Floor(b) + 1 : b;
                        if (!integer)
                        {
                            unmapped.Add($"{what}: exclusive minimum {b} is checked as inclusive.");
                        }
                    }
                }

                if (schema["exclusiveMaximum"] is { } exMax)
                {
                    var bound = ExampleValues.Dec(exMax) ?? (IsTrue(exMax) ? maximum : null);
                    if (bound is { } b)
                    {
                        maximum = integer ? Math.Ceiling(b) - 1 : b;
                        if (!integer)
                        {
                            unmapped.Add($"{what}: exclusive maximum {b} is checked as inclusive.");
                        }
                    }
                }

                return p with { Type = integer ? ParameterType.Integer : ParameterType.Number, Minimum = minimum, Maximum = maximum };
            case "boolean":
                return p with { Type = ParameterType.Boolean };
            case "string" or null:
                if (type is null && schema.Count > 0 && schema.Any(k => k.Key is "oneOf" or "anyOf" or "not"))
                {
                    unmapped.Add($"{what}: composed schema (oneOf/anyOf/not) – bound as a string.");
                }

                p = p with { Type = ParameterType.String, MinLength = Int(schema["minLength"]), MaxLength = Int(schema["maxLength"]) };
                if (Text(schema["pattern"]) is { } pattern)
                {
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
                        p = p with { Pattern = pattern };
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        unmapped.Add($"{what}: pattern '{pattern}' (not supported by the non-backtracking regex engine).");
                    }
                }

                switch (format)
                {
                    case null:
                        return p;
                    case "date":
                        return Typed(p, ParameterType.Date);
                    case "date-time":
                        return Typed(p, ParameterType.DateTime);
                    case "uuid":
                        return Typed(p, ParameterType.Guid);
                    case "email" or "idn-email":
                        return p with { Format = ParameterFormat.Email };
                    case "uri" or "url" or "iri":
                        return p with { Format = ParameterFormat.Uri };
                    case "ipv4":
                        return p with { Format = ParameterFormat.Ipv4 };
                    case "ipv6":
                        return p with { Format = ParameterFormat.Ipv6 };
                    case "time":
                        return p with { Format = ParameterFormat.Time };
                    case "binary" when p.Source == ParameterSource.Form:
                        return p with { Type = ParameterType.File, MinLength = null, MaxLength = null, Pattern = null, AllowedValues = null };
                    case var other when !BenignFormats.Contains(other):
                        unmapped.Add($"{what}: format '{other}' is not checked.");
                        return p;
                    default:
                        return p;
                }

            default:
                unmapped.Add($"{what}: type '{type}' – bound as a string.");
                return p with { Type = ParameterType.String };
        }
    }

    // Date, date-time and uuid are types of their own and take no string constraints.
    private static ParameterDefinition Typed(ParameterDefinition p, ParameterType type) =>
        p with { Type = type, MinLength = null, MaxLength = null, Pattern = null };

    /// <summary>Fixes combinations the compiler rejects, so the skeleton validates.</summary>
    private static ParameterDefinition Finish(ParameterDefinition p, string what, List<string> unmapped)
    {
        if (p.Required && p.Default is not null)
        {
            unmapped.Add($"{what}: default of a required value.");
            p = p with { Default = null };
        }

        if (p.Source == ParameterSource.Route && p.Type is ParameterType.Array or ParameterType.Object)
        {
            unmapped.Add($"{what}: route values must be scalar – bound as a string.");
            p = p with { Type = ParameterType.String, ItemType = null, MinItems = null, MaxItems = null, Schema = null };
        }

        return p;
    }

    private static (string? MediaType, JsonObject? Media) PickMedia(JsonObject content)
    {
        foreach (var (mediaType, media) in content)
        {
            var type = mediaType.Split(';')[0].Trim().ToLowerInvariant();
            if (type is "application/json" || type.EndsWith("+json", StringComparison.Ordinal))
            {
                return ("application/json", media as JsonObject);
            }
        }

        foreach (var form in (string[])["multipart/form-data", "application/x-www-form-urlencoded"])
        {
            if (content.FirstOrDefault(c => string.Equals(c.Key.Split(';')[0].Trim(), form, StringComparison.OrdinalIgnoreCase)) is { Value: JsonObject media })
            {
                return (form, media);
            }
        }

        return (null, null);
    }

    private static (JsonObject? Schema, JsonNode? Example) Response(JsonObject operation, Resolver resolver, List<string> unmapped)
    {
        if (operation["responses"] is not JsonObject responses)
        {
            return (null, null);
        }

        var success = responses.Where(r => r.Key.StartsWith('2')).OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Value).FirstOrDefault();
        if (resolver.Resolve(success, unmapped) is not JsonObject response || response["content"] is not JsonObject content ||
            PickMedia(content) is not ("application/json", { } media))
        {
            return (null, null);
        }

        // Documentation only – references are inlined, the rest is kept as written.
        var schema = resolver.Inline(media["schema"], unmapped) as JsonObject;
        var example = media["example"]?.DeepClone()
            ?? (media["examples"] as JsonObject)?.Select(e => resolver.Resolve(e.Value, unmapped)?["value"]).FirstOrDefault(v => v is not null)?.DeepClone();
        return (schema, example);
    }

    private static JsonNode? ExampleOf(JsonObject parameter) =>
        parameter["example"]?.DeepClone()
        ?? (parameter["examples"] as JsonObject)?.Select(e => e.Value?["value"]).FirstOrDefault(v => v is not null)?.DeepClone();

    /// <summary>A copy of <paramref name="schema"/> the built-in validator accepts; dropped keywords are reported.</summary>
    private static JsonObject Sanitize(JsonObject schema, Resolver resolver, string what, List<string> unmapped, string[] skip, int depth = 0)
    {
        var result = new JsonObject();
        if (depth > 32)
        {
            unmapped.Add($"{what}: schema nested too deeply.");
            return result;
        }

        schema = Merge(schema, resolver, unmapped);
        foreach (var (keyword, value) in schema)
        {
            if (skip.Contains(keyword))
            {
                continue;
            }

            switch (keyword)
            {
                case "properties" when value is JsonObject properties:
                    var copy = new JsonObject();
                    foreach (var (name, property) in properties)
                    {
                        copy[name] = resolver.Resolve(property, unmapped) is JsonObject p ? Sanitize(p, resolver, $"{what}.{name}", unmapped, [], depth + 1) : new JsonObject();
                    }

                    result["properties"] = copy;
                    break;
                case "items" when value is JsonObject or JsonValue:
                    result["items"] = resolver.Resolve(value, unmapped) is JsonObject items ? Sanitize(items, resolver, $"{what}[]", unmapped, [], depth + 1) : value.DeepClone();
                    break;
                case "additionalProperties":
                    result["additionalProperties"] = resolver.Resolve(value, unmapped) is JsonObject additional
                        ? Sanitize(additional, resolver, what, unmapped, [], depth + 1)
                        : value?.DeepClone();
                    break;
                case "format":
                    if (Text(value) is { } format && StringFormats.IsKnown(format))
                    {
                        result["format"] = format;
                    }
                    else if (Text(value) is { } other && !BenignFormats.Contains(other))
                    {
                        unmapped.Add($"{what}: format '{other}' is not checked.");
                    }

                    break;
                case "example":
                    result["examples"] = new JsonArray(value?.DeepClone());
                    break;
                case "nullable":
                    break;
                case "exclusiveMinimum" or "exclusiveMaximum" when value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False:
                    // OpenAPI 3.0 flag form: turn the bound into the 3.1 number form.
                    var bound = keyword == "exclusiveMinimum" ? "minimum" : "maximum";
                    if (IsTrue(flag) && schema[bound] is { } limit)
                    {
                        result[keyword] = limit.DeepClone();
                        result.Remove(bound);
                        skip = [.. skip, bound];
                    }

                    break;
                case "pattern" or "patternProperties":
                    unmapped.Add($"{what}: '{keyword}' inside an object schema (use a separate parameter with a pattern).");
                    break;
                case "xml" or "externalDocs" or "discriminator":
                    break;
                default:
                    if (SchemaKeywords.Contains(keyword) || SchemaAnnotations.Contains(keyword))
                    {
                        result[keyword] = value?.DeepClone();
                    }
                    else
                    {
                        unmapped.Add($"{what}: schema keyword '{keyword}'.");
                    }

                    break;
            }
        }

        if (Bool(schema["nullable"]) && result["type"] is JsonValue singleType)
        {
            result["type"] = new JsonArray(singleType.DeepClone(), "null");
        }

        return result;
    }

    /// <summary>Folds <c>allOf</c> of object schemas into one schema; other compositions are left for the caller to report.</summary>
    private static JsonObject Merge(JsonObject schema, Resolver resolver, List<string> unmapped)
    {
        if (schema["allOf"] is not JsonArray parts)
        {
            return schema;
        }

        var merged = new JsonObject();
        foreach (var (key, value) in schema.Where(k => k.Key != "allOf"))
        {
            merged[key] = value?.DeepClone();
        }

        foreach (var part in parts)
        {
            if (resolver.Resolve(part, unmapped) is not JsonObject partSchema)
            {
                continue;
            }

            partSchema = Merge(partSchema, resolver, unmapped);
            foreach (var (key, value) in partSchema)
            {
                switch (key)
                {
                    case "properties" when value is JsonObject properties:
                        var target = merged["properties"] as JsonObject ?? (JsonObject)(merged["properties"] = new JsonObject());
                        foreach (var (name, property) in properties)
                        {
                            target[name] = property?.DeepClone();
                        }

                        break;
                    case "required" when value is JsonArray required:
                        var list = merged["required"] as JsonArray ?? (JsonArray)(merged["required"] = new JsonArray());
                        foreach (var name in Strings(required).Where(n => !Strings(list).Contains(n)))
                        {
                            list.Add(name);
                        }

                        break;
                    default:
                        merged[key] ??= value?.DeepClone();
                        break;
                }
            }
        }

        return merged;
    }

    private static string Route(string? prefix, string path)
    {
        var route = path.Trim();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            route = "/" + prefix.Trim().Trim('/') + "/" + route.TrimStart('/');
        }

        return route.Length > 1 ? route.TrimEnd('/') : route;
    }

    /// <summary>A valid, unique parameter name: <c>X-Tenant-Id</c> → <c>xTenantId</c>, <c>1st</c> → <c>_1st</c>.</summary>
    private static string UniqueName(string name, HashSet<string> used)
    {
        string candidate;
        if (ParameterName().IsMatch(name))
        {
            candidate = name;
        }
        else
        {
            var builder = new StringBuilder();
            var upper = false;
            foreach (var c in name)
            {
                if (char.IsAsciiLetterOrDigit(c) || c == '_')
                {
                    builder.Append(upper && builder.Length > 0 ? char.ToUpperInvariant(c) : builder.Length == 0 ? char.ToLowerInvariant(c) : c);
                    upper = false;
                }
                else
                {
                    upper = true;
                }
            }

            candidate = builder.Length == 0 ? "value" : char.IsAsciiDigit(builder[0]) ? "_" + builder : builder.ToString();
        }

        var unique = candidate;
        for (var i = 2; !used.Add(unique); i++)
        {
            unique = candidate + i;
        }

        return unique;
    }

    private static string? TypeOf(JsonObject schema) => schema["type"] switch
    {
        JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
        JsonArray types => Strings(types).FirstOrDefault(t => t != "null"),
        _ when schema["properties"] is not null => "object",
        _ when schema["items"] is not null => "array",
        _ => null,
    };

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { Length: > 0 } text ? text : null;

    private static IEnumerable<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(Text).OfType<string>();

    private static bool Bool(JsonNode? node) => IsTrue(node);

    private static bool IsTrue(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.True;

    private static int? Int(JsonNode? node) => ExampleValues.Dec(node) is { } number ? (int)Math.Clamp(number, 0, int.MaxValue) : null;

    /// <summary>Resolves local references (<c>#/components/…</c>) of the document.</summary>
    private sealed class Resolver(JsonObject document)
    {
        public JsonNode? Resolve(JsonNode? node, List<string> unmapped)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (node is JsonObject obj && Text(obj["$ref"]) is { } reference)
            {
                if (!seen.Add(reference))
                {
                    unmapped.Add($"circular reference '{reference}'.");
                    return null;
                }

                node = Find(reference, unmapped);
            }

            return node;
        }

        /// <summary>A deep copy with every local reference replaced by its target; cycles become <c>{}</c>.</summary>
        public JsonNode? Inline(JsonNode? node, List<string> unmapped) => Inline(node, unmapped, [], 0);

        private JsonNode? Inline(JsonNode? node, List<string> unmapped, HashSet<string> path, int depth)
        {
            if (depth > 64)
            {
                return new JsonObject();
            }

            switch (node)
            {
                case JsonObject obj when Text(obj["$ref"]) is { } reference:
                    if (!path.Add(reference))
                    {
                        unmapped.Add($"circular reference '{reference}' in the response schema.");
                        return new JsonObject();
                    }

                    var inlined = Inline(Find(reference, unmapped), unmapped, path, depth + 1);
                    path.Remove(reference);
                    return inlined;
                case JsonObject obj:
                    return new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Inline(p.Value, unmapped, path, depth + 1))));
                case JsonArray array:
                    return new JsonArray(array.Select(i => Inline(i, unmapped, path, depth + 1)).ToArray());
                default:
                    return node?.DeepClone();
            }
        }

        private JsonNode? Find(string reference, List<string> unmapped)
        {
            if (!reference.StartsWith("#/", StringComparison.Ordinal))
            {
                unmapped.Add($"external reference '{reference}'.");
                return null;
            }

            JsonNode? current = document;
            foreach (var raw in reference[2..].Split('/'))
            {
                var segment = Uri.UnescapeDataString(raw).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                current = current is JsonObject obj ? obj[segment] : null;
                if (current is null)
                {
                    unmapped.Add($"unresolved reference '{reference}'.");
                    return null;
                }
            }

            return current;
        }
    }
}
