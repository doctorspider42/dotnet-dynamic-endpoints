using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace DynamicEndpoints.Runtime;

internal sealed record RequestRejection(DynamicRequestRejection Reason, int StatusCode, string Title, string? Detail = null);

internal sealed record BindingResult(
    JsonObject Values,
    ValidationErrors Errors,
    IReadOnlyDictionary<string, IReadOnlyList<IFormFile>> Files,
    RequestRejection? Rejection = null);

/// <summary>Reads parameters from route, query, headers and body and converts them to their declared JSON types.</summary>
internal sealed class ParameterBinder(IOptions<DynamicEndpointsOptions> options)
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<IFormFile>> NoFiles = new Dictionary<string, IReadOnlyList<IFormFile>>();

    public async Task<BindingResult> BindAsync(HttpContext context, CompiledEndpoint endpoint)
    {
        var values = new JsonObject();
        var errors = new ValidationErrors(options.Value.Messages);

        JsonObject? body = null;
        IFormCollection? form = null;
        if (endpoint.HasBody)
        {
            var (parsed, rejection) = await ReadJsonAsync(context.Request, errors);
            if (rejection is not null)
            {
                return new BindingResult(values, errors, NoFiles, rejection);
            }

            body = parsed;
        }
        else if (endpoint.HasForm)
        {
            var (parsed, rejection) = await ReadFormAsync(context.Request, errors);
            if (rejection is not null)
            {
                return new BindingResult(values, errors, NoFiles, rejection);
            }

            form = parsed;
        }

        Dictionary<string, IReadOnlyList<IFormFile>>? files = null;
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
                ParameterSource.Form when DynamicEndpointMetadata.IsFile(p) => FromFiles(p, form!, errors, files ??= new(StringComparer.Ordinal)),
                ParameterSource.Form => FromFormField(p, form!, errors),
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
                    errors.Add(source, ErrorMessage.Of($"required.{p.Source.ToString().ToLowerInvariant()}", DynamicValidationCodes.Required, source));
                }

                continue;
            }

            values[p.Name] = value;
        }

        return new BindingResult(values, errors, files ?? NoFiles);
    }

    private async Task<(JsonObject? Body, RequestRejection? Rejection)> ReadJsonAsync(HttpRequest request, ValidationErrors errors)
    {
        var limit = options.Value.MaxRequestBodySize;
        if (request.ContentLength > limit)
        {
            return (null, TooLarge(limit, errors));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > limit)
            {
                return (null, TooLarge(limit, errors));
            }
        }

        if (buffer.Length == 0)
        {
            return (new JsonObject(), null);
        }

        if (!request.HasJsonContentType())
        {
            return (null, UnsupportedMediaType("body.unsupportedMediaType.json", errors));
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (!Utf8.IsValid(bytes))
        {
            return (null, InvalidBody("body.invalidEncoding", errors));
        }

        try
        {
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { MaxDepth = options.Value.MaxJsonDepth });
            if (node is not JsonObject obj)
            {
                return (null, InvalidBody("body.notObject", errors));
            }

            // Escapes such as "\ud800" are valid JSON but no valid text – System.Text.Json only notices when a string is read.
            EnsureDecodable(obj);
            return (obj, null);
        }
        catch (JsonException)
        {
            return (null, InvalidBody("body.invalidJson", errors));
        }
        catch (InvalidOperationException)
        {
            return (null, InvalidBody("body.invalidEncoding", errors));
        }
    }

    private async Task<(IFormCollection? Form, RequestRejection? Rejection)> ReadFormAsync(HttpRequest request, ValidationErrors errors)
    {
        var limit = options.Value.MaxFormBodySize;
        if (request.ContentLength > limit)
        {
            return (null, TooLarge(limit, errors));
        }

        if (!request.HasFormContentType)
        {
            var canHaveBody = request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
                ?? request.ContentLength is not (0 or null);
            return request.ContentLength == 0 || !canHaveBody
                ? (FormCollection.Empty, null)
                : (null, UnsupportedMediaType("body.unsupportedMediaType.form", errors));
        }

        // Files are streamed to temporary files by ASP.NET Core beyond a small in-memory threshold – the body is never
        // held in memory as a whole. The wrapper enforces the overall limit; FormOptions limits single sections only.
        var original = request.Body;
        request.Body = new LengthLimitedStream(original, limit);
        try
        {
            var formOptions = new FormOptions
            {
                MultipartBodyLengthLimit = limit,
                ValueLengthLimit = (int)Math.Min(limit, int.MaxValue),
            };
            return (await request.ReadFormAsync(formOptions, request.HttpContext.RequestAborted), null);
        }
        catch (PayloadTooLargeException)
        {
            return (null, TooLarge(limit, errors));
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, TooLarge(limit, errors));
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            return (null, TooLarge(limit, errors));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException && !request.HttpContext.RequestAborted.IsCancellationRequested)
        {
            return (null, InvalidBody("body.invalidForm", errors));
        }
        finally
        {
            request.Body = original;
        }
    }

    private static void EnsureDecodable(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    EnsureDecodable(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    EnsureDecodable(child);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                value.GetValue<string>();
                break;
        }
    }

    private static RequestRejection InvalidBody(string key, ValidationErrors errors)
    {
        errors.Add("body", ErrorMessage.Of(key, DynamicValidationCodes.InvalidBody));
        return new RequestRejection(DynamicRequestRejection.InvalidBody, StatusCodes.Status400BadRequest, errors.Format(Title("title.validation")));
    }

    private static RequestRejection TooLarge(long limit, ValidationErrors errors) => new(
        DynamicRequestRejection.PayloadTooLarge,
        StatusCodes.Status413PayloadTooLarge,
        errors.Format(Title("title.payloadTooLarge")),
        errors.Format(ErrorMessage.Of("body.tooLarge", DynamicValidationCodes.InvalidBody, limit)));

    private static RequestRejection UnsupportedMediaType(string key, ValidationErrors errors) => new(
        DynamicRequestRejection.UnsupportedMediaType,
        StatusCodes.Status415UnsupportedMediaType,
        errors.Format(Title("title.unsupportedMediaType")),
        errors.Format(ErrorMessage.Of(key, DynamicValidationCodes.InvalidBody)));

    private static ErrorMessage Title(string key) => ErrorMessage.Of(key, string.Empty);

    private static JsonNode? FromFormField(ParameterDefinition p, IFormCollection form, ValidationErrors errors)
    {
        var raw = form[p.EffectiveSourceName];
        if (raw.Count == 0 && form.Files.GetFiles(p.EffectiveSourceName).Count > 0)
        {
            errors.Add(p.EffectiveSourceName, ErrorMessage.Of("type.text", DynamicValidationCodes.Type));
            return null;
        }

        return FromStrings(p, raw, splitCommas: false, errors);
    }

    private static JsonNode? FromFiles(ParameterDefinition p, IFormCollection form, ValidationErrors errors, Dictionary<string, IReadOnlyList<IFormFile>> files)
    {
        var key = p.EffectiveSourceName;

        // Browsers send an empty part for a file input nobody picked a file for.
        var uploaded = form.Files.GetFiles(key).Where(f => f.Length > 0 || !string.IsNullOrEmpty(f.FileName)).ToList();
        if (uploaded.Count == 0)
        {
            if (form[key].Any(v => !string.IsNullOrEmpty(v)))
            {
                errors.Add(key, ErrorMessage.Of("type.file", DynamicValidationCodes.Type));
            }

            return null;
        }

        if (p.Type == ParameterType.File && uploaded.Count > 1)
        {
            errors.Add(key, ErrorMessage.Of("multipleValues", DynamicValidationCodes.MultipleValues));
            return null;
        }

        files[p.Name] = uploaded;
        return p.Type == ParameterType.File
            ? Describe(uploaded[0])
            : new JsonArray(uploaded.Select(f => (JsonNode)Describe(f)).ToArray());
    }

    private static JsonObject Describe(IFormFile file) => new()
    {
        ["fileName"] = file.FileName,
        ["contentType"] = file.ContentType,
        ["length"] = file.Length,
    };

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
                var itemType = p.ItemType ?? ParameterType.String;
                var converted = Convert(item, itemType);
                if (converted is null)
                {
                    errors.Add(key, InvalidValue(item, itemType));
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
            errors.Add(key, ErrorMessage.Of("multipleValues", DynamicValidationCodes.MultipleValues));
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
            errors.Add(key, InvalidValue(value, p.Type));
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
        ParameterType.Object or ParameterType.Array or ParameterType.File => null,
        _ => JsonValue.Create(value),
    };

    private static ErrorMessage InvalidValue(string value, ParameterType type) =>
        ErrorMessage.Of($"type.{type.ToString().ToLowerInvariant()}", DynamicValidationCodes.Type, value);
}

/// <summary>Thrown by <see cref="LengthLimitedStream"/> when more than the allowed number of bytes is read.</summary>
internal sealed class PayloadTooLargeException() : IOException("The request body is too large.");

/// <summary>Read-only wrapper that fails once more than <c>limit</c> bytes were read.</summary>
internal sealed class LengthLimitedStream(Stream inner, long limit) : Stream
{
    private long _read;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Count(int read)
    {
        _read += read;
        return _read > limit ? throw new PayloadTooLargeException() : read;
    }
}
