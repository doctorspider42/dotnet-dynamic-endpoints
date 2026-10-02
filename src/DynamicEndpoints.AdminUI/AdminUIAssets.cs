using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;

namespace DynamicEndpoints.AdminUI;

internal sealed record AdminUIAsset(byte[] Content, string ContentType, string ETag);

/// <summary>The panel's files, embedded in the assembly – no static files middleware, nothing to copy.</summary>
internal static partial class AdminUIAssets
{
    private static readonly Dictionary<string, AdminUIAsset> Assets = Load();
    private static readonly string Index = Encoding.UTF8.GetString(Read("index.html"));

    // Changes with every change of the files, so browsers never combine an old script with a new page.
    private static readonly string Version = Convert.ToHexStringLower(
        SHA256.HashData(Assets.Values.SelectMany(a => a.Content).ToArray()))[..12];

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        // The default encoder escapes <, > and &, so the JSON can't close the <script> element it sits in.
        Encoder = JavaScriptEncoder.Default,
    };

    public static bool TryGet(string file, out AdminUIAsset asset) => Assets.TryGetValue(file, out asset!);

    public static string RenderIndex(string pathBase, string root, string adminApiPath, DynamicEndpointsAdminUIOptions options,
        RouteValueDictionary? routeValues = null)
    {
        // Route parameters of the panel's pattern (/tenants/{tenant}/admin) are filled into the paths; others stay for the script.
        string Fill(string path) => Placeholder().Replace(path, m =>
            routeValues is not null && routeValues.TryGetValue(m.Groups[1].Value, out var value) && value?.ToString() is { Length: > 0 } text
                ? Uri.EscapeDataString(text)
                : m.Value);

        string? Url(string? path)
        {
            if (path is null)
            {
                return null;
            }

            path = Fill(path);
            return Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? path : pathBase + "/" + path.TrimStart('/');
        }

        var config = JsonSerializer.Serialize(new
        {
            title = options.Title,
            apiPath = Url(adminApiPath),
            pathBase,
            swaggerUrl = Url(options.SwaggerUrl),
            openApiUrl = Url(options.OpenApiUrl),
            tenantSwaggerUrl = Url(options.TenantSwaggerUrl),
            tenantOpenApiUrl = Url(options.TenantOpenApiUrl),
        }, ConfigJson);

        return Index
            .Replace("__TITLE__", HtmlEncoder.Default.Encode(options.Title), StringComparison.Ordinal)
            .Replace("__BASE__", HtmlEncoder.Default.Encode(pathBase + Fill(root)), StringComparison.Ordinal)
            .Replace("__VERSION__", Version, StringComparison.Ordinal)
            .Replace("__CONFIG__", config, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();

    private static Dictionary<string, AdminUIAsset> Load()
    {
        var types = new Dictionary<string, string>
        {
            ["admin.js"] = "text/javascript; charset=utf-8",
            ["admin.css"] = "text/css; charset=utf-8",
        };

        return types.ToDictionary(t => t.Key, t =>
        {
            var content = Read(t.Key);
            return new AdminUIAsset(content, t.Value, $"\"{Convert.ToHexStringLower(SHA256.HashData(content))[..16]}\"");
        }, StringComparer.OrdinalIgnoreCase);
    }

    private static byte[] Read(string file)
    {
        using var stream = typeof(AdminUIAssets).Assembly.GetManifestResourceStream($"DynamicEndpoints.AdminUI.{file}")
            ?? throw new InvalidOperationException($"The admin UI file '{file}' is missing from the assembly.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
