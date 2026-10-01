using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DynamicEndpoints.Processing;
using DynamicEndpoints.Validation;
using DynamicEndpoints.Validation.Engine;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace DynamicEndpoints.Runtime;

internal sealed record CompilationResult(CompiledEndpoint? Endpoint, ValidationErrors Errors);

/// <summary>
/// Validates a definition as a whole and turns it into a <see cref="CompiledEndpoint"/>.
/// Anything that can be checked up front is checked here, so a bad definition never reaches the routing table.
/// </summary>
internal sealed partial class DynamicEndpointCompiler(
    ProcessorRegistry processors,
    ValidatorRegistry validators,
    RouteInspector routes,
    IServiceScopeFactory scopeFactory,
    IServiceProvider services,
    IOptions<DynamicEndpointsOptions> options)
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNameRegex();

    public async Task<CompilationResult> CompileAsync(DynamicEndpointDefinition definition, CancellationToken cancellationToken)
    {
        var d = definition;
        var errors = new ValidationErrors();

        if (!options.Value.AllowedMethods.Contains(d.Method))
        {
            errors.Add("method", $"HTTP method '{d.Method}' is not allowed. Allowed: {string.Join(", ", options.Value.AllowedMethods)}.");
        }

        var pattern = ParseRoute(d.Route, errors);
        var parameters = CompileParameters(d, pattern, errors);
        var schema = BuildValidationSchema(d, errors);
        var rules = CompileRules(d, errors);
        var processorName = await ValidateProcessorAsync(d, errors, cancellationToken);
        await ValidateSecurityAsync(d, errors);

        await using var scope = scopeFactory.CreateAsyncScope();
        for (var i = 0; i < parameters.Count; i++)
        {
            parameters[i] = parameters[i] with
            {
                Validators = await CompileValidatorsAsync(scope.ServiceProvider, parameters[i].Definition.Validators, $"parameters[{i}].validators", parameters[i].Definition, errors),
            };
        }

        var requestValidators = await CompileValidatorsAsync(scope.ServiceProvider, d.Validators, "validators", parameter: null, errors);

        if (errors.HasErrors)
        {
            return new CompilationResult(null, errors);
        }

        var compiled = new CompiledEndpoint
        {
            Definition = d,
            ProcessorName = processorName!,
            RoutePattern = pattern!,
            RouteKey = $"{d.Method} {RouteKeys.Normalize(pattern!)}",
            Parameters = parameters,
            ParametersByName = parameters.ToDictionary(p => p.Definition.Name, StringComparer.Ordinal),
            ValidationSchema = schema!,
            Rules = rules,
            Validators = requestValidators,
            Configuration = d.ProcessorConfig?.DeepClone() as JsonObject ?? new JsonObject(),
        };
        return new CompilationResult(compiled, errors);
    }

    private RoutePattern? ParseRoute(string route, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(route) || route == "/")
        {
            errors.Add("route", "Route is required and cannot be the application root.");
            return null;
        }

        if (routes.FindReservedPrefix(route) is { } prefix)
        {
            errors.Add("route", $"Routes under '{prefix}' are reserved by the application.");
        }

        if (!routes.HasRequiredPrefix(route))
        {
            errors.Add("route", $"Routes must start with {string.Join(" or ", options.Value.RequiredRoutePrefixes.Select(p => $"'{p}'"))}.");
        }

        try
        {
            return RoutePatternFactory.Parse(route);
        }
        catch (RoutePatternException ex)
        {
            errors.Add("route", ex.Message);
            return null;
        }
    }

    private static List<CompiledParameter> CompileParameters(DynamicEndpointDefinition d, RoutePattern? pattern, ValidationErrors errors)
    {
        var compiled = new List<CompiledParameter>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bindings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bodyAllowed = d.Method is "POST" or "PUT" or "PATCH";

        for (var i = 0; i < d.Parameters.Count; i++)
        {
            var p = d.Parameters[i];
            var key = $"parameters[{i}]";

            if (!ParameterNameRegex().IsMatch(p.Name))
            {
                errors.Add($"{key}.name", "Name must start with a letter or underscore and contain only letters, digits and underscores.");
            }
            else if (!names.Add(p.Name))
            {
                errors.Add($"{key}.name", $"Parameter '{p.Name}' is defined more than once.");
            }

            if (!bindings.Add($"{p.Source}:{p.EffectiveSourceName}"))
            {
                errors.Add($"{key}.sourceName", $"Another parameter already binds {p.Source.ToString().ToLowerInvariant()} '{p.EffectiveSourceName}'.");
            }

            ValidateSource(p, key, bodyAllowed, pattern, errors);
            ValidateConstraints(p, key, errors);

            if (p.Format is not null &&
                !(p.Type == ParameterType.String || (p.Type == ParameterType.Array && (p.ItemType ?? ParameterType.String) == ParameterType.String && p.Schema is null)))
            {
                errors.Add($"{key}.format", "A format can only be used with String parameters or arrays of strings.");
            }

            Regex? regex = null;
            if (p.Pattern is not null)
            {
                var isString = p.Type == ParameterType.String ||
                    (p.Type == ParameterType.Array && (p.ItemType ?? ParameterType.String) == ParameterType.String && p.Schema is null);
                if (!isString)
                {
                    errors.Add($"{key}.pattern", "A pattern can only be used with String parameters or arrays of strings.");
                }
                else
                {
                    try
                    {
                        regex = new Regex(p.Pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, RegexTimeout);
                    }
                    catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                    {
                        errors.Add($"{key}.pattern", $"Invalid or unsupported regular expression: {ex.Message}");
                    }
                }
            }

            try
            {
                SchemaEvaluation.Build(ParameterSchemas.Build(p, forDocumentation: false));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                errors.Add($"{key}.schema", $"Invalid schema: {ex.Message}");
            }

            compiled.Add(new CompiledParameter(p, regex));
        }

        if (d.Parameters.Any(p => p.Source == ParameterSource.Body) && d.Parameters.Any(p => p.Source == ParameterSource.Form))
        {
            errors.Add("parameters", "An endpoint reads either a JSON body ('Body' parameters) or a form ('Form' parameters), not both.");
        }

        if (pattern is not null)
        {
            foreach (var routeParameter in pattern.Parameters)
            {
                if (!d.Parameters.Any(p => p.Source == ParameterSource.Route &&
                        string.Equals(p.EffectiveSourceName, routeParameter.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("parameters", $"Route parameter '{{{routeParameter.Name}}}' needs a parameter definition with source 'Route'.");
                }
            }
        }

        return compiled;
    }

    private static void ValidateSource(ParameterDefinition p, string key, bool bodyAllowed, RoutePattern? pattern, ValidationErrors errors)
    {
        switch (p.Source)
        {
            case ParameterSource.Body when !bodyAllowed:
                errors.Add($"{key}.source", "Body parameters are only allowed for POST, PUT and PATCH endpoints.");
                break;
            case ParameterSource.Form when !bodyAllowed:
                errors.Add($"{key}.source", "Form parameters are only allowed for POST, PUT and PATCH endpoints.");
                break;
            case ParameterSource.Route when p.Type is ParameterType.Array or ParameterType.Object:
                errors.Add($"{key}.type", "Route parameters must be scalar values.");
                break;
            case ParameterSource.Route when pattern is not null && pattern.GetParameter(p.EffectiveSourceName) is null:
                errors.Add($"{key}.name", $"The route template does not contain '{{{p.EffectiveSourceName}}}'.");
                break;
        }

        if (p.Type == ParameterType.Object && p.Source != ParameterSource.Body)
        {
            errors.Add($"{key}.type", "Object parameters can only be read from the body.");
        }

        if (p.Type == ParameterType.Array && p.Source != ParameterSource.Body && p.ItemType is ParameterType.Array or ParameterType.Object)
        {
            errors.Add($"{key}.itemType", "Query and header arrays can only contain scalar values.");
        }

        if (p.ItemType is not null && p.Type != ParameterType.Array)
        {
            errors.Add($"{key}.itemType", "Item type can only be set for Array parameters.");
        }

        var isFile = DynamicEndpointMetadata.IsFile(p);
        if (isFile && p.Source != ParameterSource.Form)
        {
            errors.Add($"{key}.type", "Files can only be read from 'Form' parameters (multipart/form-data).");
        }

        if (p.Schema is not null)
        {
            if (p.Type is not (ParameterType.Object or ParameterType.Array))
            {
                errors.Add($"{key}.schema", "A custom schema can only be used with Object and Array parameters.");
            }
            else if (ContainsRegex(p.Schema))
            {
                // Schema regexes would run on the backtracking engine – keep regexes in the 'pattern' constraint.
                errors.Add($"{key}.schema", "Regular expressions ('pattern', 'patternProperties') are not allowed in custom schemas.");
            }
        }
    }

    private static void ValidateConstraints(ParameterDefinition p, string key, ValidationErrors errors)
    {
        if (DynamicEndpointMetadata.IsFile(p))
        {
            if (p.MinLength is not null || p.MaxLength is not null || p.Minimum is not null || p.Maximum is not null ||
                p.Pattern is not null || p.Format is not null || p.AllowedValues is not null || p.Schema is not null)
            {
                errors.Add($"{key}.type", "Files support only 'maxFileSize', 'allowedContentTypes' and (for arrays) item counts.");
            }

            if (p.Default is not null)
            {
                errors.Add($"{key}.default", "Files cannot have a default value.");
            }

            if (p.MaxFileSize <= 0)
            {
                errors.Add($"{key}.maxFileSize", "Maximum file size must be positive.");
            }

            foreach (var contentType in p.AllowedContentTypes ?? [])
            {
                if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) || parsed.MediaType.Value is not { } media ||
                    media.Length == 0 || media.Contains(';') || (media.StartsWith('*') && media != "*/*"))
                {
                    errors.Add($"{key}.allowedContentTypes", $"'{contentType}' is not a content type such as 'application/pdf' or 'image/*'.");
                }
            }
        }
        else if (p.MaxFileSize is not null || p.AllowedContentTypes is not null)
        {
            errors.Add($"{key}.maxFileSize", "'maxFileSize' and 'allowedContentTypes' can only be set for File parameters.");
        }

        if (p.MinLength < 0 || p.MaxLength < 0)
        {
            errors.Add($"{key}.minLength", "Lengths cannot be negative.");
        }

        if (p.MinLength > p.MaxLength)
        {
            errors.Add($"{key}.minLength", "Minimum length is greater than maximum length.");
        }

        if (p.Minimum > p.Maximum)
        {
            errors.Add($"{key}.minimum", "Minimum is greater than maximum.");
        }

        if (p.MinItems < 0 || p.MaxItems < 0)
        {
            errors.Add($"{key}.minItems", "Item counts cannot be negative.");
        }

        if (p.MinItems > p.MaxItems)
        {
            errors.Add($"{key}.minItems", "Minimum item count is greater than maximum item count.");
        }

        if ((p.MinItems is not null || p.MaxItems is not null) && p.Type != ParameterType.Array)
        {
            errors.Add($"{key}.minItems", "Item counts can only be set for Array parameters.");
        }

        if (p.Required && p.Default is not null)
        {
            errors.Add($"{key}.default", "A required parameter cannot have a default value.");
        }
    }

    private static JsonObject? BuildValidationSchema(DynamicEndpointDefinition d, ValidationErrors errors)
    {
        // Built even when other problems were found, so admins see default-value errors in the same round trip.
        // Parameters that are themselves broken (bad custom schema, duplicate name) are left out.
        var usable = d.Parameters
            .Select((p, i) => (p, i))
            .Where(x => !errors.Contains($"parameters[{x.i}].schema") && !errors.Contains($"parameters[{x.i}].name"))
            .Select(x => x.p)
            .ToList();

        JsonObject schema;
        try
        {
            schema = SchemaEvaluation.Build(ParameterSchemas.BuildObject(usable, forDocumentation: false, useSourceNames: false));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            errors.Add("parameters", $"Invalid parameter schema: {ex.Message}");
            return null;
        }

        // Defaults must satisfy the constraints of their own parameter.
        var defaults = new JsonObject();
        foreach (var p in usable.Where(p => p.Default is not null))
        {
            defaults[p.Name] = p.Default!.DeepClone();
        }

        if (defaults.Count > 0)
        {
            var defaultErrors = new ValidationErrors();
            SchemaEvaluation.Evaluate(schema, defaults, name => name, defaultErrors);
            foreach (var (path, messages) in defaultErrors.ToDictionary())
            {
                var name = path.Split('.', '[')[0];
                var index = d.Parameters.ToList().FindIndex(p => p.Name == name);
                foreach (var message in messages)
                {
                    errors.Add(index >= 0 ? $"parameters[{index}].default" : "parameters", message);
                }
            }
        }

        return schema;
    }

    private static List<CompiledRule> CompileRules(DynamicEndpointDefinition d, ValidationErrors errors)
    {
        var compiled = new List<CompiledRule>();
        for (var i = 0; i < d.Rules.Count; i++)
        {
            var r = d.Rules[i];
            var key = $"rules[{i}]";
            if (string.IsNullOrWhiteSpace(r.Message))
            {
                errors.Add($"{key}.message", "A rule needs an error message.");
            }

            if (r.Parameter is not null && !d.Parameters.Any(p => p.Name == r.Parameter))
            {
                errors.Add($"{key}.parameter", $"Parameter '{r.Parameter}' is not defined.");
            }

            if (r.Condition is null)
            {
                errors.Add($"{key}.condition", "A rule needs a JsonLogic condition.");
                continue;
            }

            var problems = JsonLogic.Validate(r.Condition);
            foreach (var problem in problems)
            {
                errors.Add($"{key}.condition", $"Invalid JsonLogic condition: {problem}");
            }

            if (problems.Count == 0)
            {
                compiled.Add(new CompiledRule(r, r.Condition.DeepClone()));
            }
        }

        return compiled;
    }

    private async Task<string?> ValidateProcessorAsync(DynamicEndpointDefinition d, ValidationErrors errors, CancellationToken cancellationToken)
    {
        var name = d.Processor ?? options.Value.DefaultProcessor;
        if (name is null)
        {
            errors.Add("processor", "A processor is required.");
            return null;
        }

        if (!processors.TryGet(name, out var descriptor))
        {
            var available = processors.All.Count == 0 ? "none registered" : string.Join(", ", processors.All.Select(p => p.Name));
            errors.Add("processor", $"Unknown processor '{name}'. Available: {available}.");
            return null;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var processor = scope.ServiceProvider.GetRequiredKeyedService<IDynamicEndpointProcessor>(descriptor.Name);
            var configuration = d.ProcessorConfig?.DeepClone() as JsonObject ?? new JsonObject();
            foreach (var message in processor.ValidateConfiguration(configuration))
            {
                errors.Add("processorConfig", message);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            errors.Add("processorConfig", $"Configuration could not be validated: {ex.Message}");
        }

        return descriptor.Name;
    }

    private async Task<IReadOnlyList<CompiledValidator>> CompileValidatorsAsync(
        IServiceProvider scopedServices,
        IReadOnlyList<ValidatorReference>? references,
        string key,
        ParameterDefinition? parameter,
        ValidationErrors errors)
    {
        var compiled = new List<CompiledValidator>();
        for (var i = 0; i < (references?.Count ?? 0); i++)
        {
            var reference = references![i];
            var itemKey = $"{key}[{i}]";
            if (!validators.TryGet(reference.Name, out var descriptor))
            {
                var available = validators.All.Count == 0 ? "none registered" : string.Join(", ", validators.All.Select(v => v.Name));
                errors.Add($"{itemKey}.name", $"Unknown validator '{reference.Name}'. Available: {available}.");
                continue;
            }

            var forParameter = parameter is not null;
            if (forParameter ? !descriptor.ForParameters : !descriptor.ForRequests)
            {
                errors.Add($"{itemKey}.name", forParameter
                    ? $"Validator '{descriptor.Name}' validates whole requests – attach it to the endpoint, not to a parameter."
                    : $"Validator '{descriptor.Name}' validates single values – attach it to a parameter.");
                continue;
            }

            if (parameter is not null && descriptor.ParameterTypes is { Count: > 0 } types && !types.Contains(parameter.Type))
            {
                errors.Add($"{itemKey}.name",
                    $"Validator '{descriptor.Name}' accepts {string.Join("/", types)} parameters, but '{parameter.Name}' is {parameter.Type}.");
                continue;
            }

            var configuration = reference.Config?.DeepClone() as JsonObject ?? new JsonObject();
            try
            {
                var validator = scopedServices.GetRequiredKeyedService<IDynamicValidator>(descriptor.Name);
                foreach (var message in validator.ValidateConfiguration((JsonObject)configuration.DeepClone()))
                {
                    errors.Add($"{itemKey}.config", message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{itemKey}.config", $"Configuration could not be validated: {ex.Message}");
            }

            compiled.Add(new CompiledValidator(descriptor.Name, configuration));
        }

        return compiled;
    }

    private async Task ValidateSecurityAsync(DynamicEndpointDefinition d, ValidationErrors errors)
    {
        if (d.AllowAnonymous && (d.RequireAuthorization || d.AuthorizationPolicy is not null))
        {
            errors.Add("allowAnonymous", "An endpoint cannot both allow anonymous access and require authorization.");
        }

        if (d.AuthorizationPolicy is { } policy)
        {
            var provider = services.GetService<IAuthorizationPolicyProvider>();
            if (provider is null)
            {
                errors.Add("authorizationPolicy", "Authorization services are not registered (call AddAuthorization).");
            }
            else if (await provider.GetPolicyAsync(policy) is null)
            {
                errors.Add("authorizationPolicy", $"Unknown authorization policy '{policy}'.");
            }
        }
    }

    private static bool ContainsRegex(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p =>
            (p.Key == "pattern" && p.Value is JsonValue) ||
            (p.Key == "patternProperties" && p.Value is JsonObject) ||
            ContainsRegex(p.Value)),
        JsonArray array => array.Any(ContainsRegex),
        _ => false,
    };
}
