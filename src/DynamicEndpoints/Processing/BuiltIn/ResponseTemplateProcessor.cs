using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using DynamicEndpoints.Processing.BuiltIn;
using Microsoft.AspNetCore.Http;

namespace DynamicEndpoints;

/// <summary>Configuration of the <c>response</c> processor.</summary>
public sealed class ResponseTemplateConfig
{
    [Range(100, 599)]
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    /// <summary>
    /// JSON template of the response. <c>"{{quantity}}"</c> keeps the parameter's JSON type, <c>"Order {{id}}"</c> becomes text,
    /// paths reach into objects and arrays (<c>{{address.city}}</c>, <c>{{items[0]}}</c>).
    /// </summary>
    public JsonNode? Body { get; set; }

    /// <summary>Text template instead of <see cref="Body"/>, e.g. <c>Hello, {{name}}!</c>. Values are inserted as is (no HTML encoding).</summary>
    public string? Text { get; set; }

    /// <summary>Default <c>application/json</c> for <see cref="Body"/>, <c>text/plain; charset=utf-8</c> for <see cref="Text"/>.</summary>
    public string? ContentType { get; set; }

    /// <summary>Response headers; values may contain <c>{name}</c> placeholders.</summary>
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// Answers with a response built from a template and the validated parameters – mock APIs, fixed answers, request-to-response
/// mapping. Register with <c>AddResponseTemplateProcessor()</c>.
/// </summary>
[DynamicProcessor("response",
    Description = "Returns a response rendered from a JSON or text template with {{name}} placeholders.",
    ConfigurationExample = """{ "statusCode": 200, "body": { "id": "{{id}}", "message": "Hello, {{name}}!" } }""")]
public sealed class ResponseTemplateProcessor : DynamicEndpointProcessor<ResponseTemplateConfig>
{
    protected override IEnumerable<string> Validate(ResponseTemplateConfig config)
    {
        if (config.Body is not null && config.Text is not null)
        {
            yield return "Set either 'body' or 'text', not both.";
        }

        foreach (var (name, value) in config.Headers ?? [])
        {
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)))
            {
                yield return $"'{name}' is not a valid header name.";
            }

            if (value.Contains('\r') || value.Contains('\n'))
            {
                yield return $"Header '{name}' can't contain line breaks.";
            }
        }
    }

    protected override Task<IResult> ProcessAsync(DynamicRequest request, ResponseTemplateConfig config)
    {
        foreach (var (name, template) in config.Headers ?? [])
        {
            request.HttpContext.Response.Headers[name] = DynamicTemplate.RenderHeader(template, request.Parameters, configuration: null);
        }

        IResult result = config switch
        {
            { Text: { } text } => Results.Text(DynamicTemplate.RenderText(text, request.Parameters),
                config.ContentType ?? "text/plain; charset=utf-8", statusCode: config.StatusCode),
            { Body: { } body } => Results.Json(DynamicTemplate.RenderJson(body, request.Parameters),
                contentType: config.ContentType, statusCode: config.StatusCode),
            _ => Results.StatusCode(config.StatusCode),
        };
        return Task.FromResult(result);
    }
}
