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

        var components = new JsonObject
        {
            ["schemas"] = new JsonObject
            {
                ["ProblemDetails"] = ProblemDetailsSchema(withErrors: false),
                ["HttpValidationProblemDetails"] = ProblemDetailsSchema(withErrors: true),
            },
        };
        if (openApi.SecuritySchemes.Count > 0)
        {
            var schemes = new JsonObject();
            foreach (var (name, scheme) in openApi.SecuritySchemes)
            {
                schemes[name] = scheme.DeepClone();
            }

            components["securitySchemes"] = schemes;
        }

        var document = new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = info,
        };
        if (openApi.SecurityRequirements.Count > 0)
        {
            document["security"] = new JsonArray(openApi.SecurityRequirements.Select(r => (JsonNode)r.DeepClone()).ToArray());
        }

        document["paths"] = paths;
        document["components"] = components;
        openApi.ConfigureDocument?.Invoke(document);
        return document;
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
        foreach (var p in d.Parameters.Where(p => p.Source is not (ParameterSource.Body or ParameterSource.Form)))
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

            if (p.Example is not null)
            {
                parameter["example"] = p.Example.DeepClone();
            }

            parameters.Add(parameter);
        }

        foreach (var header in openApi.Headers)
        {
            var documented = d.Parameters.Any(p => p.Source == ParameterSource.Header &&
                string.Equals(p.EffectiveSourceName, header.Name, StringComparison.OrdinalIgnoreCase));
            if (documented || header.AppliesTo?.Invoke(d) == false)
            {
                continue;
            }

            var parameter = new JsonObject
            {
                ["name"] = header.Name,
                ["in"] = "header",
                ["required"] = header.Required,
                ["schema"] = header.Schema?.DeepClone() ?? new JsonObject { ["type"] = "string" },
            };
            if (header.Description is not null)
            {
                parameter["description"] = header.Description;
            }

            if (header.Example is not null)
            {
                parameter["example"] = header.Example.DeepClone();
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
                    ["application/json"] = WithExample(
                        new JsonObject { ["schema"] = ParameterSchemas.BuildObject(bodyParameters, forDocumentation: true, useSourceNames: true) },
                        RequestExample(d, bodyParameters)),
                },
            };
        }

        var formParameters = d.Parameters.Where(p => p.Source == ParameterSource.Form).ToList();
        if (formParameters.Count > 0)
        {
            var schema = ParameterSchemas.BuildObject(formParameters, forDocumentation: true, useSourceNames: true);
            var multipart = WithExample(new JsonObject { ["schema"] = schema }, RequestExample(d, formParameters));
            var encoding = new JsonObject();
            foreach (var p in formParameters.Where(p => p.AllowedContentTypes is { Count: > 0 }))
            {
                encoding[p.EffectiveSourceName] = new JsonObject { ["contentType"] = string.Join(", ", p.AllowedContentTypes!) };
            }

            if (encoding.Count > 0)
            {
                multipart["encoding"] = encoding;
            }

            var content = new JsonObject { ["multipart/form-data"] = multipart };
            if (!formParameters.Any(DynamicEndpointMetadata.IsFile))
            {
                content["application/x-www-form-urlencoded"] = WithExample(new JsonObject { ["schema"] = schema.DeepClone() }, RequestExample(d, formParameters));
            }

            operation["requestBody"] = new JsonObject
            {
                ["required"] = formParameters.Any(p => p.Required),
                ["content"] = content,
            };
        }

        var success = new JsonObject { ["description"] = "Success" };
        if (d.ResponseSchema is not null || d.ResponseExample is not null)
        {
            var media = new JsonObject();
            if (d.ResponseSchema is not null)
            {
                media["schema"] = d.ResponseSchema.DeepClone();
            }

            success["content"] = new JsonObject { ["application/json"] = WithExample(media, d.ResponseExample?.DeepClone()) };
        }

        var responses = new JsonObject
        {
            ["200"] = success,
            ["400"] = ProblemResponse("Validation failed", ValidationProblemRef),
        };
        if (bodyParameters.Count > 0 || formParameters.Count > 0)
        {
            responses["413"] = ProblemResponse("Payload too large", ProblemRef);
            responses["415"] = ProblemResponse("Unsupported media type", ProblemRef);
        }

        if (!d.AllowAnonymous && (d.RequireAuthorization || d.AuthorizationPolicy is not null))
        {
            responses["401"] = new JsonObject { ["description"] = "Unauthorized" };
            responses["403"] = new JsonObject { ["description"] = "Forbidden" };
        }

        if (!d.AllowAnonymous)
        {
            var security = openApi.OperationSecurity
                .Where(s => s.AppliesTo?.Invoke(d) != false)
                .Select(s => (JsonNode)s.Requirement.DeepClone())
                .ToArray();
            if (security.Length > 0)
            {
                operation["security"] = new JsonArray(security);
                responses["401"] ??= new JsonObject { ["description"] = "Unauthorized" };
            }
        }
        else if (openApi.SecurityRequirements.Count > 0)
        {
            // Anonymous endpoints opt out of the document-wide requirements.
            operation["security"] = new JsonArray();
        }

        operation["responses"] = responses;
        operation["x-dynamic-endpoint"] = new JsonObject
        {
            ["id"] = d.Id.ToString(),
            ["revision"] = d.Revision,
            ["processor"] = endpoint.ProcessorName,
        };
        openApi.ConfigureOperation?.Invoke(operation, d);
        return operation;
    }

    private static JsonObject WithExample(JsonObject media, JsonNode? example)
    {
        if (example is not null)
        {
            media["example"] = example;
        }

        return media;
    }

    private static JsonObject? RequestExample(DynamicEndpointDefinition d, IReadOnlyList<ParameterDefinition> parameters)
    {
        if (d.RequestExample is not null)
        {
            return (JsonObject)d.RequestExample.DeepClone();
        }

        var example = new JsonObject();
        foreach (var p in parameters.Where(p => p.Example is not null))
        {
            example[p.EffectiveSourceName] = p.Example!.DeepClone();
        }

        return example.Count > 0 ? example : null;
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
