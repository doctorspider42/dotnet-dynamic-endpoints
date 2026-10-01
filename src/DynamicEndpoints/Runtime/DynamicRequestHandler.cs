using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Runtime;

/// <summary>The request delegate shared by every dynamic endpoint: bind → validate → process.</summary>
internal sealed class DynamicRequestHandler(ParameterBinder binder, ILogger<DynamicRequestHandler> logger)
{
    public async Task HandleAsync(HttpContext context, CompiledEndpoint endpoint)
    {
        var definition = endpoint.Definition;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["DynamicEndpointId"] = definition.Id,
            ["DynamicEndpointVersion"] = definition.Version,
        });

        var binding = await binder.BindAsync(context, endpoint);
        if (binding.Failure is not null)
        {
            await binding.Failure.ExecuteAsync(context);
            return;
        }

        // Cheap checks first, all reported together; expensive request-level validators only for otherwise valid input.
        var errors = binding.Errors;
        RequestValidator.ValidateStructure(endpoint, binding.Values, errors);
        await RunParameterValidatorsAsync(context, endpoint, binding.Values, errors);
        if (!errors.HasErrors)
        {
            RequestValidator.ValidateRules(endpoint, binding.Values, errors);
        }

        if (!errors.HasErrors)
        {
            foreach (var validator in endpoint.Validators)
            {
                await RunAsync(context, endpoint, validator, null, binding.Values, errors);
            }
        }

        if (errors.HasErrors)
        {
            logger.LogDebug("Request to dynamic endpoint {Method} {Route} failed validation.", definition.Method, definition.Route);
            await Results.ValidationProblem(errors.ToDictionary()).ExecuteAsync(context);
            return;
        }

        var processor = context.RequestServices.GetRequiredKeyedService<IDynamicEndpointProcessor>(endpoint.ProcessorName);
        var result = await processor.ProcessAsync(new DynamicRequest(endpoint, binding.Values, context));
        await (result ?? Results.Empty).ExecuteAsync(context);
    }

    private static async Task RunParameterValidatorsAsync(HttpContext context, CompiledEndpoint endpoint, JsonObject values, ValidationErrors errors)
    {
        foreach (var parameter in endpoint.Parameters)
        {
            var definition = parameter.Definition;
            if (parameter.Validators.Count == 0 || !values.ContainsKey(definition.Name))
            {
                continue;
            }

            foreach (var validator in parameter.Validators)
            {
                // A value that already failed built-in checks (or a previous validator) is not worth validating further.
                if (errors.HasErrorsFor(definition.EffectiveSourceName))
                {
                    break;
                }

                await RunAsync(context, endpoint, validator, definition, values, errors);
            }
        }
    }

    private static ValueTask RunAsync(
        HttpContext context,
        CompiledEndpoint endpoint,
        CompiledValidator validator,
        ParameterDefinition? parameter,
        JsonObject values,
        ValidationErrors errors)
    {
        var instance = context.RequestServices.GetRequiredKeyedService<IDynamicValidator>(validator.Name);
        return instance.ValidateAsync(new DynamicValidationContext(endpoint, validator, parameter, values, context, errors));
    }
}
