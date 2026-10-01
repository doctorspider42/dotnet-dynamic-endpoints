using System.Text;
using System.Text.Json.Nodes;
using DynamicEndpoints.Runtime;
using Microsoft.Extensions.Options;

namespace DynamicEndpoints;

/// <summary>Produces an OpenAPI 3.1 document describing the currently active dynamic endpoints.</summary>
public interface IDynamicOpenApiDocumentProvider
{
    JsonObject GetDocument();
}

internal sealed class DynamicOpenApiDocumentProvider(
    DynamicEndpointRuntime runtime,
    IOptions<DynamicEndpointsOptions> options) : IDynamicOpenApiDocumentProvider
{
    private const string ValidationProblemRef = "#/components/schemas/HttpValidationProblemDetails";
    private const string ProblemRef = "#/components/schemas/ProblemDetails";

    public JsonObject GetDocument()
    {
        var openApi = options.Value.OpenApi;
        var paths = new JsonObject();
        var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var endpoints = runtime.ActiveEndpoints()
            .OrderBy(e => e.Definition.Route, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Definition.Method, StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            var path = RouteKeys.ToOpenApiPath(endpoint.RoutePattern);
            if (paths[path] is not JsonObject pathItem)
            {
                paths[path] = pathItem = new JsonObject();
            }

            pathItem[endpoint.Definition.Method.ToLowerInvariant()] = BuildOperation(endpoint, openApi, operationIds);
        }

        var info = new JsonObject { ["title"] = openApi.Title, ["version"] = openApi.Version };
        if (openApi.Description is not null)
        {
            info["description"] = openApi.Description;
        }

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = info,
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = new JsonObject
                {
                    ["ProblemDetails"] = ProblemDetailsSchema(withErrors: false),
                    ["HttpValidationProblemDetails"] = ProblemDetailsSchema(withErrors: true),
                },
            },
        };
    }

    private static JsonObject BuildOperation(CompiledEndpoint endpoint, DynamicEndpointsOpenApiOptions openApi, HashSet<string> operationIds)
    {
        var d = endpoint.Definition;
        var operation = new JsonObject
        {
            // OpenAPI tags drive the sections in Swagger UI – exactly one per endpoint, so it is never listed twice.
            ["tags"] = new JsonArray(d.Group ?? openApi.DefaultGroup),
            ["summary"] = d.Name ?? $"{d.Method} {d.Route}",
            ["operationId"] = OperationId(d, operationIds),
        };

        var description = BuildDescription(d);
        if (description.Length > 0)
        {
            operation["description"] = description;
        }

        var parameters = new JsonArray();
        foreach (var p in d.Parameters.Where(p => p.Source != ParameterSource.Body))
        {
            var parameter = new JsonObject
            {
                ["name"] = p.EffectiveSourceName,
                ["in"] = p.Source switch
                {
                    ParameterSource.Route => "path",
                    ParameterSource.Header => "header",
                    _ => "query",
                },
                ["required"] = p.Source == ParameterSource.Route || p.Required,
                ["schema"] = ParameterSchemas.Build(p, forDocumentation: true),
            };
            if (p.Description is not null)
            {
                parameter["description"] = p.Description;
            }

            parameters.Add(parameter);
        }

        if (parameters.Count > 0)
        {
            operation["parameters"] = parameters;
        }

        var bodyParameters = d.Parameters.Where(p => p.Source == ParameterSource.Body).ToList();
        if (bodyParameters.Count > 0)
        {
            operation["requestBody"] = new JsonObject
            {
                ["required"] = bodyParameters.Any(p => p.Required),
                ["content"] = new JsonObject
                {
                    ["application/json"] = new JsonObject
                    {
                        ["schema"] = ParameterSchemas.BuildObject(bodyParameters, forDocumentation: true, useSourceNames: true),
                    },
                },
            };
        }

        var success = new JsonObject { ["description"] = "Success" };
        if (d.ResponseSchema is not null)
        {
            success["content"] = new JsonObject
            {
                ["application/json"] = new JsonObject { ["schema"] = d.ResponseSchema.DeepClone() },
            };
        }

        var responses = new JsonObject
        {
            ["200"] = success,
            ["400"] = ProblemResponse("Validation failed", ValidationProblemRef),
        };
        if (bodyParameters.Count > 0)
        {
            responses["413"] = ProblemResponse("Payload too large", ProblemRef);
            responses["415"] = ProblemResponse("Unsupported media type", ProblemRef);
        }

        if (!d.AllowAnonymous && (d.RequireAuthorization || d.AuthorizationPolicy is not null))
        {
            responses["401"] = new JsonObject { ["description"] = "Unauthorized" };
            responses["403"] = new JsonObject { ["description"] = "Forbidden" };
        }

        operation["responses"] = responses;
        operation["x-dynamic-endpoint"] = new JsonObject
        {
            ["id"] = d.Id.ToString(),
            ["version"] = d.Version,
            ["processor"] = endpoint.ProcessorName,
        };
        return operation;
    }

    private static string BuildDescription(DynamicEndpointDefinition d)
    {
        var builder = new StringBuilder(d.Description);
        if (d.Rules.Count > 0)
        {
            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append("**Business rules**\n");
            foreach (var rule in d.Rules)
            {
                builder.Append("\n- ").Append(rule.Message);
            }
        }

        var validatorNames = d.Validators.Select(v => v.Name)
            .Concat(d.Parameters.SelectMany(p => (p.Validators ?? []).Select(v => $"{v.Name} ({p.EffectiveSourceName})")))
            .ToList();
        if (validatorNames.Count > 0)
        {
            builder.Append(builder.Length > 0 ? "\n\n" : string.Empty)
                .Append("**Custom validation:** ").Append(string.Join(", ", validatorNames));
        }

        if (d.AuthorizationPolicy is not null)
        {
            builder.Append(builder.Length > 0 ? "\n\n" : string.Empty).Append($"Requires authorization policy `{d.AuthorizationPolicy}`.");
        }

        return builder.ToString();
    }

    private static string OperationId(DynamicEndpointDefinition d, HashSet<string> used)
    {
        var source = d.Name ?? $"{d.Method.ToLowerInvariant()} {d.Route}";
        var builder = new StringBuilder();
        var upper = d.Name is not null;
        foreach (var c in source)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            else
            {
                upper = true;
            }
        }

        var id = builder.Length > 0 ? builder.ToString() : "operation";
        var candidate = id;
        for (var i = 2; !used.Add(candidate); i++)
        {
            candidate = id + i;
        }

        return candidate;
    }

    private static JsonObject ProblemResponse(string description, string schemaRef) => new()
    {
        ["description"] = description,
        ["content"] = new JsonObject
        {
            ["application/problem+json"] = new JsonObject
            {
                ["schema"] = new JsonObject { ["$ref"] = schemaRef },
            },
        },
    };

    private static JsonObject ProblemDetailsSchema(bool withErrors)
    {
        var properties = new JsonObject
        {
            ["type"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["title"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["status"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
            ["detail"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            ["instance"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
        };
        if (withErrors)
        {
            properties["errors"] = new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject { ["type"] = "string" },
                },
            };
        }

        return new JsonObject { ["type"] = "object", ["properties"] = properties };
    }
}
