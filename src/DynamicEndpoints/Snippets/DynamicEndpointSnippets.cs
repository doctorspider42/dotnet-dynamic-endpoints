using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Runtime;
using DynamicEndpoints.Snippets;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints;

/// <summary>Generates example requests and ready-to-run client snippets for endpoint definitions (admin API: <c>GET /{id}/snippets</c>).</summary>
public interface IDynamicEndpointSnippetGenerator
{
    /// <summary>
    /// Builds an example request from parameter examples, defaults, allowed values and constraints. Optional parameters that have a
    /// default and no example are left out. <paramref name="baseUrl"/> is the application's address, e.g. <c>https://api.example.com</c>.
    /// </summary>
    DynamicEndpointRequestExample CreateExample(DynamicEndpointDefinition definition, string baseUrl);

    /// <summary>The example request plus curl, HTTPie and C# <c>HttpClient</c> snippets.</summary>
    DynamicEndpointSnippets Generate(DynamicEndpointDefinition definition, string baseUrl);

    /// <summary>
    /// The example request as <paramref name="tenant"/> sends it – for shared endpoints; an endpoint of a tenant always uses its own.
    /// With multi-tenancy the tenant is filled into the tenant route prefix and sent in the header of a <see cref="HeaderTenantResolver"/>.
    /// </summary>
    DynamicEndpointRequestExample CreateExample(DynamicEndpointDefinition definition, string baseUrl, string? tenant) =>
        CreateExample(definition, baseUrl);

    /// <summary>The example request of <paramref name="tenant"/> plus curl, HTTPie and C# <c>HttpClient</c> snippets.</summary>
    DynamicEndpointSnippets Generate(DynamicEndpointDefinition definition, string baseUrl, string? tenant) =>
        Generate(definition, baseUrl);
}

/// <summary>A name/value pair of a query string or header.</summary>
public sealed record DynamicEndpointExampleValue(string Name, string Value);

/// <summary>A field of an example form body; <see cref="FileName"/> is set for uploads.</summary>
public sealed record DynamicEndpointExampleFormField(string Name, string? Value, string? FileName = null, string? ContentType = null)
{
    public bool IsFile => FileName is not null;
}

/// <summary>An example request of a dynamic endpoint.</summary>
public sealed record DynamicEndpointRequestExample
{
    public required string Method { get; init; }

    /// <summary>Absolute URL with route values and query string filled in.</summary>
    public required string Url { get; init; }

    public IReadOnlyList<DynamicEndpointExampleValue> Query { get; init; } = [];

    /// <summary>Headers to send – parameters, documented common headers and placeholders for credentials (<c>&lt;token&gt;</c>).</summary>
    public IReadOnlyList<DynamicEndpointExampleValue> Headers { get; init; } = [];

    /// <summary><c>application/json</c>, <c>multipart/form-data</c>, <c>application/x-www-form-urlencoded</c> or <c>null</c> without a body.</summary>
    public string? ContentType { get; init; }

    /// <summary>JSON body.</summary>
    public JsonNode? Body { get; init; }

    /// <summary>Form body fields.</summary>
    public IReadOnlyList<DynamicEndpointExampleFormField> Form { get; init; } = [];
}

public sealed record DynamicEndpointSnippets(DynamicEndpointRequestExample Request, string Curl, string HttpIe, string CSharp);

internal sealed class DynamicEndpointSnippetGenerator(
    IOptions<DynamicEndpointsOptions> options,
    IEnumerable<IDynamicEndpointTenantResolver> tenantResolvers) : IDynamicEndpointSnippetGenerator
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public DynamicEndpointRequestExample CreateExample(DynamicEndpointDefinition definition, string baseUrl) =>
        CreateExample(definition, baseUrl, tenant: null);

    public DynamicEndpointRequestExample CreateExample(DynamicEndpointDefinition definition, string baseUrl, string? tenant)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(baseUrl);
        var d = DefinitionNormalizer.Normalize(definition);
        tenant = d.Tenant ?? tenant;
        var included = d.Parameters.Where(p => p.Required || p.Example is not null || p.Default is null).ToList();

        var query = new List<DynamicEndpointExampleValue>();
        foreach (var p in included.Where(p => p.Source == ParameterSource.Query))
        {
            var value = ExampleValues.For(p);
            if (value is JsonArray items)
            {
                query.AddRange(items.Select(i => new DynamicEndpointExampleValue(p.EffectiveSourceName, ExampleValues.ToText(i))));
            }
            else
            {
                query.Add(new DynamicEndpointExampleValue(p.EffectiveSourceName, ExampleValues.ToText(value)));
            }
        }

        var headers = new List<DynamicEndpointExampleValue>();
        foreach (var p in included.Where(p => p.Source == ParameterSource.Header))
        {
            var value = ExampleValues.For(p);
            headers.Add(new DynamicEndpointExampleValue(p.EffectiveSourceName,
                value is JsonArray items ? string.Join(",", items.Select(ExampleValues.ToText)) : ExampleValues.ToText(value)));
        }

        var openApi = options.Value.OpenApi;
        foreach (var header in openApi.Headers.Where(h => h.Required && h.AppliesTo?.Invoke(d) != false))
        {
            if (!headers.Any(h => string.Equals(h.Name, header.Name, StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new DynamicEndpointExampleValue(header.Name, header.Example is null ? "<value>" : ExampleValues.ToText(header.Example)));
            }
        }

        AddCredentials(d, openApi, headers);
        baseUrl = AddTenant(baseUrl.TrimEnd('/'), tenant, headers);

        string? contentType = null;
        JsonNode? body = null;
        var form = new List<DynamicEndpointExampleFormField>();
        var bodyParameters = d.Parameters.Where(p => p.Source == ParameterSource.Body).ToList();
        if (bodyParameters.Count > 0)
        {
            contentType = "application/json";
            if (d.RequestExample is not null)
            {
                body = d.RequestExample.DeepClone();
            }
            else
            {
                var obj = new JsonObject();
                foreach (var p in included.Where(p => p.Source == ParameterSource.Body))
                {
                    obj[p.EffectiveSourceName] = ExampleValues.For(p);
                }

                body = obj;
            }
        }

        var formParameters = d.Parameters.Where(p => p.Source == ParameterSource.Form).ToList();
        if (formParameters.Count > 0)
        {
            contentType = formParameters.Any(DynamicEndpointMetadata.IsFile) ? "multipart/form-data" : "application/x-www-form-urlencoded";
            foreach (var p in included.Where(p => p.Source == ParameterSource.Form))
            {
                if (DynamicEndpointMetadata.IsFile(p))
                {
                    var (fileName, fileType) = ExampleValues.File(p);
                    form.Add(new DynamicEndpointExampleFormField(p.EffectiveSourceName, null, fileName, fileType));
                    continue;
                }

                var value = d.RequestExample?[p.EffectiveSourceName] ?? ExampleValues.For(p);
                if (value is JsonArray items)
                {
                    form.AddRange(items.Select(i => new DynamicEndpointExampleFormField(p.EffectiveSourceName, ExampleValues.ToText(i))));
                }
                else
                {
                    form.Add(new DynamicEndpointExampleFormField(p.EffectiveSourceName, ExampleValues.ToText(value)));
                }
            }
        }

        var url = new StringBuilder(baseUrl.TrimEnd('/')).Append(Path(d));
        for (var i = 0; i < query.Count; i++)
        {
            url.Append(i == 0 ? '?' : '&')
                .Append(Uri.EscapeDataString(query[i].Name)).Append('=').Append(Uri.EscapeDataString(query[i].Value));
        }

        return new DynamicEndpointRequestExample
        {
            Method = d.Method,
            Url = url.ToString(),
            Query = query,
            Headers = headers,
            ContentType = contentType,
            Body = body,
            Form = form,
        };
    }

    public DynamicEndpointSnippets Generate(DynamicEndpointDefinition definition, string baseUrl) =>
        Generate(definition, baseUrl, tenant: null);

    public DynamicEndpointSnippets Generate(DynamicEndpointDefinition definition, string baseUrl, string? tenant)
    {
        var request = CreateExample(definition, baseUrl, tenant);
        return new DynamicEndpointSnippets(request, Curl(request), HttpIe(request), CSharp(request));
    }

    // With multi-tenancy a request carries its tenant: in the tenant route prefix and/or the tenant header.
    private string AddTenant(string baseUrl, string? tenant, List<DynamicEndpointExampleValue> headers)
    {
        var tenancy = options.Value.Tenancy;
        if (!tenancy.Enabled)
        {
            return baseUrl;
        }

        tenant = DynamicEndpointsTenancyOptions.IsValidTenant(tenant) ? tenant : null;
        if (tenant is not null && tenantResolvers.OfType<HeaderTenantResolver>().FirstOrDefault() is { } resolver &&
            !headers.Any(h => string.Equals(h.Name, resolver.HeaderName, StringComparison.OrdinalIgnoreCase)))
        {
            headers.Insert(0, new DynamicEndpointExampleValue(resolver.HeaderName, tenant));
        }

        // Without a tenant the prefix keeps its placeholder, like the OpenAPI document of the shared endpoints.
        return tenancy is { RoutePrefix: { } prefix, RouteParameter: { } parameter }
            ? baseUrl + (tenant is null ? prefix : prefix.Replace("{" + parameter + "}", Uri.EscapeDataString(tenant), StringComparison.OrdinalIgnoreCase))
            : baseUrl;
    }

    private static void AddCredentials(DynamicEndpointDefinition d, DynamicEndpointsOpenApiOptions openApi, List<DynamicEndpointExampleValue> headers)
    {
        if (d.AllowAnonymous)
        {
            return;
        }

        void Add(string name, string value)
        {
            if (!headers.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                headers.Add(new DynamicEndpointExampleValue(name, value));
            }
        }

        var documented = false;
        foreach (var requirement in openApi.OperationSecurity.Where(s => s.AppliesTo?.Invoke(d) != false))
        {
            foreach (var schemeName in requirement.Requirement.Select(r => r.Key))
            {
                if (!openApi.SecuritySchemes.TryGetValue(schemeName, out var scheme))
                {
                    continue;
                }

                var type = scheme["type"]?.GetValue<string>();
                if (type == "apiKey" && scheme["in"]?.GetValue<string>() == "header" && scheme["name"]?.GetValue<string>() is { } header)
                {
                    Add(header, "<api-key>");
                    documented = true;
                }
                else if (type == "http" && string.Equals(scheme["scheme"]?.GetValue<string>(), "basic", StringComparison.OrdinalIgnoreCase))
                {
                    Add("Authorization", "Basic <credentials>");
                    documented = true;
                }
                else if (type is "http" or "oauth2" or "openIdConnect")
                {
                    Add("Authorization", "Bearer <token>");
                    documented = true;
                }
            }
        }

        if (!documented && (d.RequireAuthorization || d.AuthorizationPolicy is not null))
        {
            Add("Authorization", "Bearer <token>");
        }
    }

    private static string Path(DynamicEndpointDefinition d)
    {
        RoutePattern pattern;
        try
        {
            pattern = RoutePatternFactory.Parse(d.Route);
        }
        catch (RoutePatternException)
        {
            return d.Route;
        }

        var path = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            var text = new StringBuilder();
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        text.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        text.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        var definition = d.Parameters.FirstOrDefault(p => p.Source == ParameterSource.Route &&
                            string.Equals(p.EffectiveSourceName, parameter.Name, StringComparison.OrdinalIgnoreCase));
                        var value = definition is null ? parameter.Name : ExampleValues.ToText(ExampleValues.For(definition));
                        text.Append(parameter.IsCatchAll
                            ? string.Join('/', value.Split('/').Select(Uri.EscapeDataString))
                            : Uri.EscapeDataString(value));
                        break;
                }
            }

            if (text.Length > 0)
            {
                path.Append('/').Append(text);
            }
        }

        return path.Length == 0 ? "/" : path.ToString();
    }

    // --- curl -------------------------------------------------------------------------------------------------------

    private static string Curl(DynamicEndpointRequestExample r)
    {
        var lines = new List<string>();
        var first = new StringBuilder("curl");
        if (r.Method != "GET")
        {
            first.Append(" -X ").Append(r.Method);
        }

        first.Append(' ').Append(Shell(r.Url));
        lines.Add(first.ToString());

        foreach (var header in r.Headers)
        {
            lines.Add($"-H {Shell($"{header.Name}: {header.Value}")}");
        }

        if (r.Body is not null)
        {
            lines.Add($"-H {Shell("Content-Type: application/json")}");
            lines.Add($"--data-raw {Shell(r.Body.ToJsonString())}");
        }

        foreach (var field in r.Form)
        {
            if (r.ContentType == "multipart/form-data")
            {
                // curl reads '@' and '<' at the start of -F values as file references – --form-string sends text as is.
                lines.Add(field.IsFile
                    ? $"-F {Shell($"{field.Name}=@{field.FileName};type={field.ContentType}")}"
                    : $"--form-string {Shell($"{field.Name}={field.Value}")}");
            }
            else
            {
                lines.Add($"--data-urlencode {Shell($"{field.Name}={field.Value}")}");
            }
        }

        return string.Join(" \\\n  ", lines);
    }

    // --- HTTPie -----------------------------------------------------------------------------------------------------

    private static string HttpIe(DynamicEndpointRequestExample r)
    {
        var lines = new List<string>();
        var first = new StringBuilder("http");
        if (r.Form.Count > 0)
        {
            first.Append(r.ContentType == "multipart/form-data" ? " --multipart" : " --form");
        }

        var raw = r.Body is not null and not JsonObject;
        if (raw)
        {
            first.Append(" --raw ").Append(Shell(r.Body!.ToJsonString()));
        }

        first.Append(' ').Append(r.Method).Append(' ').Append(Shell(r.Url));
        lines.Add(first.ToString());

        foreach (var header in r.Headers)
        {
            lines.Add(Shell(header.Value.Length == 0 ? $"{HttpIeKey(header.Name)};" : $"{HttpIeKey(header.Name)}:{header.Value}"));
        }

        if (r.Body is JsonObject body)
        {
            foreach (var (name, value) in body)
            {
                lines.Add(value is JsonValue v && v.GetValueKind() == JsonValueKind.String
                    ? Shell($"{HttpIeKey(name)}={v.GetValue<string>()}")
                    : Shell($"{HttpIeKey(name)}:={value?.ToJsonString() ?? "null"}"));
            }
        }

        foreach (var field in r.Form)
        {
            lines.Add(field.IsFile
                ? Shell($"{HttpIeKey(field.Name)}@{field.FileName};type={field.ContentType}")
                : Shell($"{HttpIeKey(field.Name)}={field.Value}"));
        }

        return string.Join(" \\\n  ", lines);
    }

    // Separators inside HTTPie request item keys are escaped with a backslash.
    private static string HttpIeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (c is '\\' or ':' or '=' or '@' or ';')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    // --- C# -----------------------------------------------------------------------------------------------------------

    private static string CSharp(DynamicEndpointRequestExample r)
    {
        var code = new StringBuilder();
        if (r.Body is not null)
        {
            code.Append("using System.Text;\n\n");
        }

        code.Append("using var client = new HttpClient();\n");
        code.Append($"using var request = new HttpRequestMessage(HttpMethod.{Method(r.Method)}, {Literal(r.Url)});\n");
        foreach (var header in r.Headers)
        {
            code.Append($"request.Headers.TryAddWithoutValidation({Literal(header.Name)}, {Literal(header.Value)});\n");
        }

        if (r.Body is not null)
        {
            var json = r.Body.ToJsonString(Indented);
            var quotes = new string('"', Math.Max(3, LongestQuoteRun(json) + 1));
            var indented = string.Join('\n', json.Split('\n').Select(l => "    " + l));
            code.Append($"request.Content = new StringContent({quotes}\n{indented}\n    {quotes}, Encoding.UTF8, \"application/json\");\n");
        }
        else if (r.ContentType == "multipart/form-data")
        {
            code.Append("var form = new MultipartFormDataContent();\n");
            var fileIndex = 0;
            foreach (var field in r.Form)
            {
                if (field.IsFile)
                {
                    var variable = fileIndex++ == 0 ? "file" : $"file{fileIndex}";
                    code.Append($"var {variable} = new StreamContent(File.OpenRead({Literal(field.FileName!)}));\n");
                    code.Append($"{variable}.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue({Literal(field.ContentType!)});\n");
                    code.Append($"form.Add({variable}, {Literal(field.Name)}, {Literal(field.FileName!)});\n");
                }
                else
                {
                    code.Append($"form.Add(new StringContent({Literal(field.Value ?? string.Empty)}), {Literal(field.Name)});\n");
                }
            }

            code.Append("request.Content = form;\n");
        }
        else if (r.Form.Count > 0)
        {
            code.Append("request.Content = new FormUrlEncodedContent(\n[\n");
            foreach (var field in r.Form)
            {
                code.Append($"    new KeyValuePair<string, string>({Literal(field.Name)}, {Literal(field.Value ?? string.Empty)}),\n");
            }

            code.Append("]);\n");
        }

        code.Append("\nusing var response = await client.SendAsync(request);\n");
        code.Append("response.EnsureSuccessStatusCode();\n");
        code.Append("var responseBody = await response.Content.ReadAsStringAsync();\n");
        return code.ToString();
    }

    private static string Method(string method) => method switch
    {
        "GET" => "Get",
        "POST" => "Post",
        "PUT" => "Put",
        "PATCH" => "Patch",
        "DELETE" => "Delete",
        "HEAD" => "Head",
        "OPTIONS" => "Options",
        _ => $"Parse({Literal(method)})",
    };

    private static int LongestQuoteRun(string text)
    {
        int longest = 0, current = 0;
        foreach (var c in text)
        {
            current = c == '"' ? current + 1 : 0;
            longest = Math.Max(longest, current);
        }

        return longest;
    }

    private static string Literal(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    // POSIX shell single quotes; a single quote itself is closed, escaped and reopened.
    private static string Shell(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
