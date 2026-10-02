using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicEndpoints.Runtime;

/// <summary>The request delegate shared by every dynamic endpoint: filters → bind → validate → process.</summary>
internal sealed class DynamicRequestHandler(ParameterBinder binder, ILogger<DynamicRequestHandler> logger)
{
    public async Task HandleAsync(HttpContext context, CompiledEndpoint endpoint)
    {
        var definition = endpoint.Definition;
        using var scope = logger.BeginScope(new Dictionary<string, object?>
        {
            ["DynamicEndpointId"] = definition.Id,
            ["DynamicEndpointRevision"] = definition.Revision,
        });

        var items = DynamicRequestItems.For(context);
        var filters = context.RequestServices.GetServices<IDynamicEndpointFilter>().ToArray();
        if (filters.Length > 0)
        {
            var requestContext = new DynamicEndpointRequestContext(context, endpoint.Metadata);
            foreach (var filter in filters)
            {
                await filter.OnRequestAsync(requestContext);
                if (requestContext.Result is { } shortCircuit)
                {
                    await shortCircuit.ExecuteAsync(context);
                    return;
                }
            }
        }

        var binding = await binder.BindAsync(context, endpoint);
        if (binding.Rejection is { } rejection)
        {
            await RejectAsync(context, endpoint, filters, binding, rejection);
            return;
        }

        // Cheap checks first, all reported together; expensive request-level validators only for otherwise valid input.
        var errors = binding.Errors;
        RequestValidator.ValidateStructure(endpoint, binding.Values, errors);
        await RunParameterValidatorsAsync(context, endpoint, binding, errors, items);
        if (!errors.HasErrors)
        {
            RequestValidator.ValidateRules(endpoint, binding.Values, errors);
        }

        if (!errors.HasErrors)
        {
            foreach (var validator in endpoint.Validators)
            {
                await RunAsync(context, endpoint, validator, null, binding, errors, items);
            }
        }

        if (errors.HasErrors)
        {
            logger.LogDebug("Request to dynamic endpoint {Method} {Route} failed validation.", definition.Method, definition.Route);
            var title = errors.Format(ErrorMessage.Of("title.validation", string.Empty));
            await RejectAsync(context, endpoint, filters, binding,
                new RequestRejection(DynamicRequestRejection.Validation, StatusCodes.Status400BadRequest, title));
            return;
        }

        var processor = context.RequestServices.GetRequiredKeyedService<IDynamicEndpointProcessor>(endpoint.ProcessorName);
        var result = await processor.ProcessAsync(new DynamicRequest(endpoint, binding.Values, binding.Files, context, items));
        await (result ?? Results.Empty).ExecuteAsync(context);
    }

    private static async Task RejectAsync(
        HttpContext context,
        CompiledEndpoint endpoint,
        IDynamicEndpointFilter[] filters,
        BindingResult binding,
        RequestRejection rejection)
    {
        var errors = binding.Errors.ToList();
        var factory = context.RequestServices.GetRequiredService<IDynamicErrorResponseFactory>();
        var result = factory.CreateResponse(new DynamicErrorContext(
            context, Kind(rejection.Reason), rejection.StatusCode, rejection.Title, rejection.Detail, errors, null));

        if (filters.Length > 0)
        {
            var failed = new DynamicValidationFailedContext(
                context, endpoint.Metadata, rejection.Reason, rejection.StatusCode, rejection.Title, rejection.Detail,
                errors, binding.Values, result);
            foreach (var filter in filters)
            {
                await filter.OnValidationFailedAsync(failed);
            }

            result = failed.Result ?? result;
        }

        await result.ExecuteAsync(context);
    }

    private static DynamicErrorKind Kind(DynamicRequestRejection reason) => reason switch
    {
        DynamicRequestRejection.InvalidBody => DynamicErrorKind.InvalidBody,
        DynamicRequestRejection.PayloadTooLarge => DynamicErrorKind.PayloadTooLarge,
        DynamicRequestRejection.UnsupportedMediaType => DynamicErrorKind.UnsupportedMediaType,
        _ => DynamicErrorKind.Validation,
    };

    private static async Task RunParameterValidatorsAsync(
        HttpContext context, CompiledEndpoint endpoint, BindingResult binding, ValidationErrors errors, DynamicRequestItems items)
    {
        foreach (var parameter in endpoint.Parameters)
        {
            var definition = parameter.Definition;
            if (parameter.Validators.Count == 0 || !binding.Values.ContainsKey(definition.Name))
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

                await RunAsync(context, endpoint, validator, definition, binding, errors, items);
            }
        }
    }

    private static ValueTask RunAsync(
        HttpContext context,
        CompiledEndpoint endpoint,
        CompiledValidator validator,
        ParameterDefinition? parameter,
        BindingResult binding,
        ValidationErrors errors,
        DynamicRequestItems items)
    {
        var instance = context.RequestServices.GetRequiredKeyedService<IDynamicValidator>(validator.Name);
        return instance.ValidateAsync(new DynamicValidationContext(endpoint, validator, parameter, binding.Values, binding.Files, context, errors, items));
    }
}
