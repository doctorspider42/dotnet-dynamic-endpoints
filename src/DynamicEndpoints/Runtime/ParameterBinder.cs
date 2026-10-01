using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DynamicEndpoints.Runtime;

internal sealed record BindingResult(JsonObject Values, ValidationErrors Errors, IResult? Failure = null);

/// <summary>Reads parameters from route, query, headers and body and converts them to their declared JSON types.</summary>
internal sealed class ParameterBinder(IOptions<DynamicEndpointsOptions> options)
{
    public async Task<BindingResult> BindAsync(HttpContext context, CompiledEndpoint endpoint)
    {
        var values = new JsonObject();
        var errors = new ValidationErrors();

        JsonObject? body = null;
        if (endpoint.HasBody)
        {
            var (parsed, failure) = await ReadBodyAsync(context.Request, errors);
            if (failure is not null)
            {
                return new BindingResult(values, errors, failure);
            }

            body = parsed;
        }

        foreach (var parameter in endpoint.Parameters)
        {
            var p = parameter.Definition;
            var source = p.EffectiveSourceName;
            JsonNode? value = p.Source switch
            {
                ParameterSource.Route => FromStrings(p, context.Request.RouteValues.TryGetValue(source, out var routeValue)
                    ? System.Convert.ToString(routeValue, CultureInfo.InvariantCulture) is { } s ? new StringValues(s) : StringValues.Empty
                    : StringValues.Empty, splitCommas: false, errors),
                ParameterSource.Query => FromStrings(p, context.Request.Query[source], splitCommas: false, errors),
                ParameterSource.Header => FromStrings(p, context.Request.Headers[source], splitCommas: true, errors),
                ParameterSource.Body => body?[source]?.DeepClone(),
                _ => null,
            };

            if (errors.Contains(source))
            {
                continue;
            }

            if (value is null)
            {
                if (p.Default is not null)
                {
                    values[p.Name] = p.Default.DeepClone();
                }
                else if (p.Required)
                {
                    errors.Add(source, $"The '{source}' {Describe(p.Source)} is required.");
                }

                continue;
            }

            values[p.Name] = value;
        }

        return new BindingResult(values, errors);
    }

    private async Task<(JsonObject? Body, IResult? Failure)> ReadBodyAsync(HttpRequest request, ValidationErrors errors)
    {
        var limit = options.Value.MaxRequestBodySize;
        if (request.ContentLength > limit)
        {
            return (null, TooLarge(limit));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return (null, TooLarge(limit));
            }
        }

        if (buffer.Length == 0)
        {
            return (new JsonObject(), null);
        }

        if (!request.HasJsonContentType())
        {
            return (null, Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "Unsupported media type",
                detail: "The request body must be sent as application/json."));
        }

        try
        {
            var node = JsonNode.Parse(
                buffer.GetBuffer().AsSpan(0, (int)buffer.Length),
                documentOptions: new JsonDocumentOptions { MaxDepth = options.Value.MaxJsonDepth });
            if (node is JsonObject obj)
            {
                return (obj, null);
            }

            errors.Add("body", "The request body must be a JSON object.");
        }
        catch (JsonException)
        {
            errors.Add("body", "The request body is not valid JSON.");
        }

        return (null, Results.ValidationProblem(errors.ToDictionary()));
    }

    private static IResult TooLarge(long limit) => Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Payload too large",
        detail: $"The request body cannot exceed {limit} bytes.");

    private static JsonNode? FromStrings(ParameterDefinition p, StringValues raw, bool splitCommas, ValidationErrors errors)
    {
        var key = p.EffectiveSourceName;
        if (p.Type == ParameterType.Array)
        {
            var items = raw
                .SelectMany(v => splitCommas ? (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries) : [v ?? string.Empty])
                .Where(v => v.Length > 0)
                .ToList();
            if (items.Count == 0)
            {
                return null;
            }

            var array = new JsonArray();
            foreach (var item in items)
            {
                var converted = Convert(item, p.ItemType ?? ParameterType.String);
                if (converted is null)
                {
                    errors.Add(key, $"The value '{item}' is not a valid {Describe(p.ItemType ?? ParameterType.String)}.");
                    return null;
                }

                array.Add(converted);
            }

            return array;
        }

        if (raw.Count == 0)
        {
            return null;
        }

        if (raw.Count > 1)
        {
            errors.Add(key, "Multiple values are not allowed.");
            return null;
        }

        var value = raw[0] ?? string.Empty;
        if (value.Length == 0 && p.Type != ParameterType.String)
        {
            return null;
        }

        var result = Convert(value, p.Type);
        if (result is null)
        {
            errors.Add(key, $"The value '{value}' is not a valid {Describe(p.Type)}.");
        }

        return result;
    }

    // Date, DateTime and Guid stay strings – the JSON Schema 'format' check validates them.
    private static JsonNode? Convert(string value, ParameterType type) => type switch
    {
        ParameterType.Integer => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? JsonValue.Create(l) : null,
        ParameterType.Number => decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? JsonValue.Create(m) : null,
        ParameterType.Boolean => value.ToLowerInvariant() switch
        {
            "true" or "1" => JsonValue.Create(true),
            "false" or "0" => JsonValue.Create(false),
            _ => null,
        },
        ParameterType.Object or ParameterType.Array => null,
        _ => JsonValue.Create(value),
    };

    private static string Describe(ParameterSource source) => source switch
    {
        ParameterSource.Route => "route value",
        ParameterSource.Query => "query parameter",
        ParameterSource.Header => "header",
        _ => "field",
    };

    private static string Describe(ParameterType type) => type switch
    {
        ParameterType.Integer => "integer",
        ParameterType.Number => "number",
        ParameterType.Boolean => "boolean (true/false)",
        _ => type.ToString().ToLowerInvariant(),
    };
}
