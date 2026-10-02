using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// Documents <c>ef-crud</c> endpoints in the generated OpenAPI document from the live EF model: the response schema of the exposed
/// fields (and the list envelope), <c>201</c>/<c>204</c>, <c>ETag</c> and <c>If-Match</c>, <c>404</c>, <c>409</c>, <c>412</c> and <c>428</c>.
/// Runs before the application's own <see cref="DynamicEndpointsOpenApiOptions.ConfigureOperation"/>.
/// </summary>
internal sealed class DynamicCrudOpenApi(IServiceProvider services) : IPostConfigureOptions<DynamicEndpointsOptions>
{
    private const string ProblemRef = "#/components/schemas/ProblemDetails";

    public void PostConfigure(string? name, DynamicEndpointsOptions options)
    {
        var configured = options.OpenApi.ConfigureOperation;
        var defaultProcessor = options.DefaultProcessor;
        options.OpenApi.ConfigureOperation = (operation, definition) =>
        {
            Describe(operation, definition, defaultProcessor);
            configured?.Invoke(operation, definition);
        };
    }

    private void Describe(JsonObject operation, DynamicEndpointDefinition definition, string? defaultProcessor)
    {
        var catalog = services.GetRequiredService<DynamicCrudCatalog>();
        if (!catalog.IsProcessor(definition.Processor ?? defaultProcessor) || definition.ProcessorConfig is null)
        {
            return;
        }

        DynamicCrudConfig? config;
        try
        {
            config = definition.ProcessorConfig.Deserialize<DynamicCrudConfig>(DynamicEndpointsJson.SerializerOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (config is null || catalog.Find(config.Entity) is not { } entity || operation["responses"] is not JsonObject responses)
        {
            return;
        }

        var item = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject(entity.Fields.Select(f => KeyValuePair.Create(f.Name, (JsonNode?)f.Schema(forResponse: true)))),
        };
        var tokens = entity.ConcurrencyTokens.Count > 0;
        var success = responses["200"] as JsonObject ?? new JsonObject { ["description"] = "Success" };
        if (definition.ResponseSchema is null && config.Operation != CrudOperation.Delete)
        {
            var schema = config.Operation == CrudOperation.List
                ? new JsonObject
                {
                    ["type"] = "object",
                    ["required"] = new JsonArray("items", "page", "pageSize"),
                    ["properties"] = new JsonObject
                    {
                        ["items"] = new JsonObject { ["type"] = "array", ["items"] = item },
                        ["page"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1 },
                        ["pageSize"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = config.MaxPageSize },
                        ["total"] = new JsonObject { ["type"] = "integer", ["description"] = "Number of matching items." },
                    },
                }
                : item;
            var media = success["content"]?["application/json"] as JsonObject ?? new JsonObject();
            media["schema"] = schema;
            success["content"] = new JsonObject { ["application/json"] = media.DeepClone() };
        }

        if (tokens && config.Operation is CrudOperation.Get or CrudOperation.Create or CrudOperation.Update or CrudOperation.Patch)
        {
            var headers = success["headers"] as JsonObject ?? new JsonObject();
            headers["ETag"] = Header("Version of the entity – send it in If-Match to update or delete exactly this version.");
            success["headers"] = headers.DeepClone();
        }

        responses.Remove("200");
        switch (config.Operation)
        {
            case CrudOperation.Create:
                success["description"] = "Created";
                var created = success["headers"] as JsonObject ?? new JsonObject();
                created["Location"] = Header("URL of the new entity.");
                success["headers"] = created.DeepClone();
                Insert(responses, "201", success);
                break;
            case CrudOperation.Delete:
                Insert(responses, "204", new JsonObject { ["description"] = "Deleted" });
                break;
            default:
                Insert(responses, "200", success);
                break;
        }

        if (config.Operation is not (CrudOperation.List or CrudOperation.Create))
        {
            responses["404"] = Problem("Not found");
        }

        if (config.Operation is CrudOperation.Update or CrudOperation.Patch or CrudOperation.Delete)
        {
            responses["409"] = Problem("Changed by another request in the meantime");
            if (tokens)
            {
                var parameters = operation["parameters"] as JsonArray ?? [];
                parameters.Add(new JsonObject
                {
                    ["name"] = "If-Match",
                    ["in"] = "header",
                    ["required"] = config.RequireIfMatch,
                    ["description"] = "ETag of the version this change is based on; 412 when the entity has changed since.",
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
                operation["parameters"] = parameters.DeepClone();
                responses["412"] = Problem("Precondition failed – the entity has changed (If-Match)");
                if (config.RequireIfMatch)
                {
                    responses["428"] = Problem("Precondition required – send If-Match");
                }
            }
        }

        // A shared endpoint of tenant data takes the tenant from the request; without it the request is rejected.
        if (definition.Tenant is null && entity.Tenant is not null && DynamicCrudTenancy.HeaderName(services) is { } tenantHeader)
        {
            var parameters = operation["parameters"] as JsonArray ?? [];
            var documented = parameters.OfType<JsonObject>().FirstOrDefault(p => p["in"]?.GetValue<string>() == "header"
                && string.Equals(p["name"]?.GetValue<string>(), tenantHeader, StringComparison.OrdinalIgnoreCase));
            if (documented is not null)
            {
                // A common header the application documents as optional is required here.
                documented["required"] = true;
                operation["parameters"] = parameters.DeepClone();
            }
            else
            {
                parameters.Add(new JsonObject
                {
                    ["name"] = tenantHeader,
                    ["in"] = "header",
                    ["required"] = true,
                    ["description"] = "Tenant whose data the request works on.",
                    ["schema"] = new JsonObject { ["type"] = "string" },
                });
                operation["parameters"] = parameters.DeepClone();
            }

            responses["404"] ??= Problem("Not found, or the request has no tenant");
        }

        var extension = operation["x-dynamic-endpoint"] as JsonObject;
        if (extension is not null)
        {
            extension["crud"] = new JsonObject { ["entity"] = entity.Name, ["operation"] = DynamicCrudValidation.Name(config.Operation) };
        }
    }

    // The success response first, as generated documents usually list it.
    private static void Insert(JsonObject responses, string status, JsonObject response)
    {
        var rest = responses.Select(r => KeyValuePair.Create(r.Key, r.Value?.DeepClone())).ToList();
        responses.Clear();
        responses[status] = response.DeepClone();
        foreach (var (key, value) in rest)
        {
            responses[key] = value;
        }
    }

    private static JsonObject Header(string description) => new()
    {
        ["description"] = description,
        ["schema"] = new JsonObject { ["type"] = "string" },
    };

    private static JsonObject Problem(string description) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = new JsonObject { ["$ref"] = ProblemRef } } },
    };
}
