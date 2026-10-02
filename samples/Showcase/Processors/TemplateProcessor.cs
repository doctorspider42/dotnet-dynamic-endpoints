using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DynamicEndpoints.Samples.Showcase.Processors;

public sealed class TemplateConfig
{
    [Required(ErrorMessage = "'template' is required.")]
    public string? Template { get; set; }

    public string ContentType { get; set; } = "text/plain; charset=utf-8";

    [Range(200, 599)]
    public int StatusCode { get; set; } = 200;
}

[DynamicProcessor("template",
    Description = "Renders a text response; {{name}} placeholders are replaced with parameter values.",
    ConfigurationExample = """{ "template": "Hello, {{name}}!", "contentType": "text/plain; charset=utf-8", "statusCode": 200 }""")]
public sealed partial class TemplateProcessor : DynamicEndpointProcessor<TemplateConfig>
{
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    protected override Task<IResult> ProcessAsync(DynamicRequest request, TemplateConfig configuration)
    {
        var body = Placeholder().Replace(configuration.Template!, match =>
            request.Parameters[match.Groups[1].Value] switch
            {
                null => string.Empty,
                JsonValue value => value.ToString(),
                var node => node.ToJsonString(),
            });

        return Task.FromResult(Results.Text(body, configuration.ContentType, statusCode: configuration.StatusCode));
    }
}
