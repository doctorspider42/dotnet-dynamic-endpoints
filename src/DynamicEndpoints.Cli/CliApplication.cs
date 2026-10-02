using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Yaml;

namespace DynamicEndpoints.Cli;

/// <summary>Where the CLI reads its environment from – replaced in tests.</summary>
internal sealed class CliEnvironment
{
    public TextWriter Out { get; init; } = Console.Out;

    public TextWriter Error { get; init; } = Console.Error;

    public Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>Handler for the admin API requests; <c>null</c> uses a regular <see cref="HttpClientHandler"/>.</summary>
    public HttpMessageHandler? Handler { get; init; }
}

internal static class CliApplication
{
    private const string Usage = """
        dynamic-endpoints – manage DynamicEndpoints through the admin REST API

        Usage: dynamic-endpoints <command> [arguments] [options]

        Commands:
          list                          List endpoints with their status
          export [<id>…]                Export all (or the given) definitions; --output <file> (.json or .yaml), --format json|yaml
          push <file>                   Import definitions (alias: import); --mode create|upsert|sync, --sync, --dry-run
          diff <file>                   Show what 'push --sync' would change; exit code 2 when there are differences
          import-openapi <file>         Create endpoint skeletons from an OpenAPI 3.x document (.json or .yaml);
                                        --processor, --processor-by-tag <tag>=<processor> (repeatable), --mock,
                                        --mode create|upsert|sync, --document-id, --route-prefix, --group, --tag (repeatable),
                                        --options <file> (import options as JSON/YAML, e.g. processor configurations),
                                        --enabled, --skip-invalid, --dry-run

        Connection (options or environment variables):
          -u, --url <url>               Admin API base URL, e.g. https://api.example.com/api/admin/endpoints   DYNAMIC_ENDPOINTS_URL
          --api-key <key>               API key sent in --api-key-header (default X-Api-Key)                   DYNAMIC_ENDPOINTS_API_KEY
          --token <token>               Bearer token (Authorization header)                                    DYNAMIC_ENDPOINTS_TOKEN
          -H, --header "Name: value"    Extra header, repeatable
          --timeout <seconds>           Request timeout, default 100

        Output:
          --json                        Print the server's JSON response instead of a summary
          -v, --verbose                 Also list unchanged endpoints and unmapped OpenAPI details

        Exit codes: 0 success · 1 rejected/invalid (nothing written) · 2 differences (diff) · 3 usage error · 4 connection/HTTP error
        """;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args, CliEnvironment environment)
    {
        CliArguments arguments;
        try
        {
            arguments = CliArguments.Parse(args);
            if (arguments.Flag("--version"))
            {
                environment.Out.WriteLine(typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
                return ExitCodes.Success;
            }

            if (arguments.Command is null or "help" || arguments.Flag("--help"))
            {
                environment.Out.WriteLine(Usage);
                return arguments.Command is null && !arguments.Flag("--help") ? ExitCodes.Usage : ExitCodes.Success;
            }

            using var client = CreateClient(arguments, environment);
            return arguments.Command switch
            {
                "list" => await ListAsync(client, arguments, environment),
                "export" => await ExportAsync(client, arguments, environment),
                "push" or "import" => await PushAsync(client, arguments, environment, diff: false),
                "diff" => await PushAsync(client, arguments, environment, diff: true),
                "import-openapi" => await ImportOpenApiAsync(client, arguments, environment),
                _ => throw new CliUsageException($"Unknown command '{arguments.Command}'. Run 'dynamic-endpoints --help'."),
            };
        }
        catch (CliUsageException ex)
        {
            environment.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Usage;
        }
        catch (HttpRequestException ex)
        {
            environment.Error.WriteLine($"error: the admin API could not be reached: {ex.Message}");
            return ExitCodes.Connection;
        }
        catch (TaskCanceledException)
        {
            environment.Error.WriteLine("error: the request to the admin API timed out.");
            return ExitCodes.Connection;
        }
        catch (UnexpectedResponseException ex)
        {
            environment.Error.WriteLine($"error: {ex.Message}");
            return ExitCodes.Connection;
        }
    }

    private static HttpClient CreateClient(CliArguments arguments, CliEnvironment environment)
    {
        var url = arguments.Value("--url") ?? environment.Variable("DYNAMIC_ENDPOINTS_URL")
            ?? throw new CliUsageException("The admin API URL is missing: pass --url or set DYNAMIC_ENDPOINTS_URL.");
        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var baseAddress) || baseAddress.Scheme is not ("http" or "https"))
        {
            throw new CliUsageException($"'{url}' is not an absolute http or https URL.");
        }

        var timeout = arguments.Value("--timeout") is { } seconds
            ? int.TryParse(seconds, out var value) && value > 0 ? TimeSpan.FromSeconds(value) : throw new CliUsageException("--timeout must be a positive number of seconds.")
            : TimeSpan.FromSeconds(100);

        var client = environment.Handler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
        client.BaseAddress = baseAddress;
        client.Timeout = timeout;
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("dynamic-endpoints-cli", "1.0"));

        if ((arguments.Value("--api-key") ?? environment.Variable("DYNAMIC_ENDPOINTS_API_KEY")) is { Length: > 0 } apiKey)
        {
            var header = arguments.Value("--api-key-header") ?? environment.Variable("DYNAMIC_ENDPOINTS_API_KEY_HEADER") ?? "X-Api-Key";
            client.DefaultRequestHeaders.TryAddWithoutValidation(header, apiKey);
        }
        else
        {
            arguments.Value("--api-key-header");
        }

        if ((arguments.Value("--token") ?? environment.Variable("DYNAMIC_ENDPOINTS_TOKEN")) is { Length: > 0 } token)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        foreach (var header in arguments.Values("--header"))
        {
            var colon = header.IndexOf(':');
            if (colon <= 0)
            {
                throw new CliUsageException($"--header '{header}' must look like 'Name: value'.");
            }

            client.DefaultRequestHeaders.Remove(header[..colon].Trim());
            client.DefaultRequestHeaders.TryAddWithoutValidation(header[..colon].Trim(), header[(colon + 1)..].Trim());
        }

        return client;
    }

    // --- list ---------------------------------------------------------------------------------------------------------

    private static async Task<int> ListAsync(HttpClient client, CliArguments arguments, CliEnvironment environment)
    {
        var json = arguments.Flag("--json");
        arguments.EnsureAllUsed();
        if (arguments.Positional.Count > 0)
        {
            throw new CliUsageException("'list' takes no arguments.");
        }

        var (_, body) = await SendAsync(client, HttpMethod.Get, "", null, [HttpStatusCode.OK]);
        if (json)
        {
            environment.Out.WriteLine(body.ToJsonString(Indented));
            return ExitCodes.Success;
        }

        var rows = (body as JsonArray ?? []).Select(s => new[]
        {
            Text(s?["status"]),
            Text(s?["definition"]?["method"]),
            Text(s?["definition"]?["route"]),
            Text(s?["definition"]?["name"]),
            Text(s?["definition"]?["id"]),
        }).ToList();
        WriteTable(environment.Out, ["STATUS", "METHOD", "ROUTE", "NAME", "ID"], rows);
        environment.Out.WriteLine($"{rows.Count} endpoint(s).");
        return ExitCodes.Success;
    }

    // --- export -------------------------------------------------------------------------------------------------------

    private static async Task<int> ExportAsync(HttpClient client, CliArguments arguments, CliEnvironment environment)
    {
        var output = arguments.Value("--output");
        var format = (arguments.Value("--format") ?? FormatOf(output) ?? "json").ToLowerInvariant();
        arguments.EnsureAllUsed();
        if (format is not ("json" or "yaml" or "yml"))
        {
            throw new CliUsageException($"Unknown format '{format}'. Use json or yaml.");
        }

        foreach (var id in arguments.Positional.Where(id => !Guid.TryParse(id, out _)))
        {
            throw new CliUsageException($"'{id}' is not an endpoint id.");
        }

        var query = string.Join("&", arguments.Positional.Select(id => "id=" + Uri.EscapeDataString(id)));
        var (_, body) = await SendAsync(client, HttpMethod.Get, query.Length > 0 ? "export?" + query : "export", null, [HttpStatusCode.OK]);
        var export = DynamicEndpointExport.FromJsonNode(body);
        var text = format == "json" ? export.ToJson() : DynamicEndpointsYaml.Write(export.ToJsonNode());

        if (output is null)
        {
            environment.Out.Write(text);
        }
        else
        {
            await File.WriteAllTextAsync(output, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            environment.Error.WriteLine($"Exported {export.Endpoints.Count} endpoint(s) to {output}.");
        }

        return ExitCodes.Success;
    }

    // --- push / diff --------------------------------------------------------------------------------------------------

    private static async Task<int> PushAsync(HttpClient client, CliArguments arguments, CliEnvironment environment, bool diff)
    {
        var sync = arguments.Flag("--sync");
        var mode = (arguments.Value("--mode") ?? (sync || diff ? "sync" : "upsert")).ToLowerInvariant();
        var dryRun = diff || arguments.Flag("--dry-run");
        var json = arguments.Flag("--json");
        var verbose = arguments.Flag("--verbose");
        var format = arguments.Value("--format");
        arguments.EnsureAllUsed();
        if (mode is not ("create" or "upsert" or "sync") || (sync && mode != "sync"))
        {
            throw new CliUsageException($"Unknown or conflicting mode '{mode}'. Use create, upsert or sync.");
        }

        var file = SingleFile(arguments, diff ? "diff" : "push");
        var export = DynamicEndpointExport.FromJsonNode(await ReadDocumentAsync(file, format));
        var (status, body) = await SendAsync(client, HttpMethod.Post, $"import?mode={mode}&dryRun={(dryRun ? "true" : "false")}",
            new StringContent(export.ToJsonNode().ToJsonString(), Encoding.UTF8, "application/json"),
            [HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity]);

        if (json)
        {
            environment.Out.WriteLine(body.ToJsonString(Indented));
        }
        else
        {
            WriteImportResult(environment.Out, body, dryRun, verbose);
        }

        if (status == HttpStatusCode.UnprocessableEntity || body["succeeded"]?.GetValue<bool>() == false)
        {
            return ExitCodes.Failed;
        }

        return diff && body["hasChanges"]?.GetValue<bool>() == true ? ExitCodes.Differences : ExitCodes.Success;
    }

    private static void WriteImportResult(TextWriter output, JsonNode body, bool dryRun, bool verbose)
    {
        foreach (var item in body["items"] as JsonArray ?? [])
        {
            var action = Text(item?["action"]);
            if (action == "Unchanged" && !verbose)
            {
                continue;
            }

            var (symbol, label) = action switch
            {
                "Create" => ("+", dryRun ? "create" : "created"),
                "Update" => ("~", dryRun ? "update" : "updated"),
                "Delete" => ("-", dryRun ? "delete" : "deleted"),
                "Skip" => ("·", "skipped"),
                "Invalid" => ("!", "invalid"),
                _ => ("=", "unchanged"),
            };
            var line = $"{symbol} {label,-9} {Text(item?["method"]),-6} {Text(item?["route"])}";
            if (Text(item?["name"]) is { Length: > 0 } name)
            {
                line += $"  ({name})";
            }

            if (item?["changes"] is JsonArray { Count: > 0 } changes)
            {
                line += $"  [{string.Join(", ", changes.Select(Text))}]";
            }

            output.WriteLine(line);
            foreach (var (key, messages) in item?["errors"] as JsonObject ?? [])
            {
                foreach (var message in messages as JsonArray ?? [])
                {
                    output.WriteLine($"      {key}: {Text(message)}");
                }
            }
        }

        var counts = $"{Count(body, "created")} to create, {Count(body, "updated")} to update, {Count(body, "deleted")} to delete, " +
            $"{Count(body, "unchanged")} unchanged, {Count(body, "skipped")} skipped, {Count(body, "invalid")} invalid.";
        if (body["succeeded"]?.GetValue<bool>() == false)
        {
            output.WriteLine($"Rejected – nothing was written. {counts}");
        }
        else
        {
            output.WriteLine(dryRun ? $"Dry run: {counts}" : $"Done: {counts.Replace("to create", "created").Replace("to update", "updated").Replace("to delete", "deleted")}");
        }
    }

    // --- import-openapi -----------------------------------------------------------------------------------------------

    private static async Task<int> ImportOpenApiAsync(HttpClient client, CliArguments arguments, CliEnvironment environment)
    {
        var dryRun = arguments.Flag("--dry-run");
        var query = new List<string> { "dryRun=" + (dryRun ? "true" : "false") };
        foreach (var (option, parameter) in new[] { ("--processor", "processor"), ("--route-prefix", "routePrefix"), ("--group", "group"), ("--document-id", "documentId") })
        {
            if (arguments.Value(option) is { } value)
            {
                query.Add($"{parameter}={Uri.EscapeDataString(value)}");
            }
        }

        if (arguments.Value("--mode") is { } mode)
        {
            mode = mode.ToLowerInvariant();
            if (mode is not ("create" or "upsert" or "sync"))
            {
                throw new CliUsageException($"Unknown mode '{mode}'. Use create, upsert or sync.");
            }

            query.Add("mode=" + mode);
        }

        query.AddRange(arguments.Values("--tag").Select(t => "tag=" + Uri.EscapeDataString(t)));
        foreach (var mapping in arguments.Values("--processor-by-tag"))
        {
            var separator = mapping.LastIndexOf('=');
            if (separator <= 0 || separator == mapping.Length - 1)
            {
                throw new CliUsageException($"--processor-by-tag '{mapping}' must look like <tag>=<processor>.");
            }

            query.Add("processorByTag=" + Uri.EscapeDataString($"{mapping[..separator]}:{mapping[(separator + 1)..]}"));
        }

        foreach (var (flag, parameter) in new[] { ("--enabled", "enabled"), ("--skip-invalid", "skipInvalid"), ("--mock", "mock") })
        {
            if (arguments.Flag(flag))
            {
                query.Add(parameter + "=true");
            }
        }

        var json = arguments.Flag("--json");
        var verbose = arguments.Flag("--verbose");
        var format = arguments.Value("--format");
        var optionsFile = arguments.Value("--options");
        arguments.EnsureAllUsed();
        var file = SingleFile(arguments, "import-openapi");
        var document = await ReadDocumentAsync(file, format) ?? throw new CliUsageException($"'{file}' is empty.");

        // Options with processor configurations (per tag, too) go in the body: { "document": …, "options": … }.
        if (optionsFile is not null)
        {
            var options = await ReadDocumentAsync(optionsFile, null) as JsonObject ?? throw new CliUsageException($"'{optionsFile}' must contain an object of import options.");
            document = new JsonObject { ["document"] = document, ["options"] = options };
        }

        var (status, body) = await SendAsync(client, HttpMethod.Post, "import/openapi?" + string.Join("&", query),
            new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json"),
            [HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity]);

        if (json)
        {
            environment.Out.WriteLine(body.ToJsonString(Indented));
        }
        else
        {
            WriteOpenApiResult(environment.Out, body, dryRun, verbose);
        }

        return status == HttpStatusCode.UnprocessableEntity || body["succeeded"]?.GetValue<bool>() == false ? ExitCodes.Failed : ExitCodes.Success;
    }

    private static void WriteOpenApiResult(TextWriter output, JsonNode body, bool dryRun, bool verbose)
    {
        foreach (var operation in body["operations"] as JsonArray ?? [])
        {
            var action = Text(operation?["action"]);
            if (action == "Unchanged" && !verbose)
            {
                continue;
            }

            var (symbol, label) = action switch
            {
                "Create" => ("+", dryRun ? "create" : "created"),
                "Update" => ("~", dryRun ? "update" : "updated"),
                "Delete" => ("-", dryRun ? "delete" : "deleted"),
                "Skip" => ("·", "skipped"),
                "Invalid" => ("!", "invalid"),
                _ => ("=", "unchanged"),
            };
            var line = $"{symbol} {label,-9} {Text(operation?["method"]),-6} {Text(operation?["path"])}";
            if (action is not ("Skip" or "Delete") && Text(operation?["processor"]) is { Length: > 0 } processor)
            {
                line += $"  → {processor}";
                if (Text(operation?["processorReason"]) is { Length: > 0 } reason)
                {
                    line += $" ({reason})";
                }
            }

            if (operation?["changes"] is JsonArray { Count: > 0 } changes)
            {
                line += $"  [{string.Join(", ", changes.Select(Text))}]";
            }

            output.WriteLine(line);
            if (action == "Skip" && Text(operation?["reason"]) is { Length: > 0 } why)
            {
                output.WriteLine($"      {why}");
            }

            foreach (var (key, messages) in operation?["errors"] as JsonObject ?? [])
            {
                foreach (var message in messages as JsonArray ?? [])
                {
                    output.WriteLine($"      {key}: {Text(message)}");
                }
            }

            if (verbose)
            {
                foreach (var note in operation?["unmapped"] as JsonArray ?? [])
                {
                    output.WriteLine($"      not mapped: {Text(note)}");
                }
            }
        }

        var counts = $"{Count(body, "created")} to create, {Count(body, "updated")} to update, {Count(body, "deleted")} to delete, " +
            $"{Count(body, "unchanged")} unchanged, {Count(body, "skipped")} skipped, {Count(body, "invalid")} invalid.";
        if (body["succeeded"]?.GetValue<bool>() == false)
        {
            output.WriteLine($"Rejected – nothing was written. {counts}");
        }
        else
        {
            output.WriteLine(dryRun ? $"Dry run: {counts}" : $"Done: {counts.Replace("to create", "created").Replace("to update", "updated").Replace("to delete", "deleted")}");
        }
    }

    // --- helpers ------------------------------------------------------------------------------------------------------

    private static async Task<(HttpStatusCode Status, JsonNode Body)> SendAsync(
        HttpClient client, HttpMethod method, string path, HttpContent? content, HttpStatusCode[] expected)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (!expected.Contains(response.StatusCode))
        {
            var detail = TryParse(text) is JsonObject problem
                ? string.Join(" ", new[] { Text(problem["title"]), Text(problem["detail"]) }.Where(t => t.Length > 0))
                : text.Length > 300 ? text[..300] : text;
            throw new UnexpectedResponseException(
                $"{method} {client.BaseAddress}{path} answered {(int)response.StatusCode} {response.ReasonPhrase}{(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        }

        return (response.StatusCode, TryParse(text) ?? throw new UnexpectedResponseException($"{method} {client.BaseAddress}{path} did not answer with JSON."));
    }

    private static async Task<JsonNode?> ReadDocumentAsync(string file, string? format)
    {
        if (!File.Exists(file))
        {
            throw new CliUsageException($"File '{file}' does not exist.");
        }

        var text = await File.ReadAllTextAsync(file);
        try
        {
            return (format ?? FormatOf(file) ?? "json").ToLowerInvariant() switch
            {
                "yaml" or "yml" => DynamicEndpointsYaml.Parse(text),
                "json" => new JsonDynamicEndpointsTextFormat().Parse(text),
                var other => throw new CliUsageException($"Unknown format '{other}'. Use json or yaml."),
            };
        }
        catch (FormatException ex)
        {
            throw new CliUsageException($"'{file}' could not be read: {ex.Message}");
        }
    }

    private static string SingleFile(CliArguments arguments, string command) => arguments.Positional switch
    {
        [var file] => file,
        [] => throw new CliUsageException($"'{command}' needs a file."),
        _ => throw new CliUsageException($"'{command}' takes exactly one file."),
    };

    private static string? FormatOf(string? file) => Path.GetExtension(file)?.ToLowerInvariant() switch
    {
        ".yaml" or ".yml" => "yaml",
        ".json" => "json",
        _ => null,
    };

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return text.Length == 0 ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int Count(JsonNode body, string name) => body[name]?.GetValue<int>() ?? 0;

    private static string Text(JsonNode? node) => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : node?.ToJsonString() ?? string.Empty;

    private static void WriteTable(TextWriter output, string[] headers, List<string[]> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        output.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        foreach (var row in rows)
        {
            output.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))).TrimEnd());
        }
    }

    private sealed class UnexpectedResponseException(string message) : Exception(message);
}
