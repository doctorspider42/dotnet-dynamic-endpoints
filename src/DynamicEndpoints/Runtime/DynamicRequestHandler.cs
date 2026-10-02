using System.Diagnostics;
using DynamicEndpoints.Diagnostics;
using DynamicEndpoints.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static DynamicEndpoints.DynamicEndpointsTelemetry;

namespace DynamicEndpoints.Runtime;

/// <summary>The request delegate shared by every dynamic endpoint: filters → bind → validate → process.</summary>
internal sealed class DynamicRequestHandler(
    ParameterBinder binder,
    DynamicEndpointsInstrumentation instrumentation,
    ILogger<DynamicRequestHandler> logger,
    DynamicEndpointTenantResolution tenants,
    IOptions<DynamicEndpointsOptions> options)
{
    public async Task HandleAsync(HttpContext context, CompiledEndpoint endpoint)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = DynamicEndpointsInstrumentation.StartActivity(Activities.Request, endpoint);
        var outcome = Outcomes.Error;
        try
        {
            outcome = await HandleCoreAsync(context, endpoint);
        }
        catch (Exception ex)
        {
            DynamicEndpointsInstrumentation.Failed(activity, ex);
            instrumentation.RecordError(endpoint, ex);
            throw;
        }
        finally
        {
            // Exceptions are answered later (error middleware, developer page) – a 500 is what the client gets.
            var statusCode = outcome == Outcomes.Error ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            activity?.SetTag(Tags.Outcome, outcome);
            instrumentation.RecordRequest(endpoint, outcome, statusCode, Stopwatch.GetElapsedTime(started));
        }
    }

    private async Task<string> HandleCoreAsync(HttpContext context, CompiledEndpoint endpoint)
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
            using var filtersActivity = DynamicEndpointsInstrumentation.StartActivity(Activities.Filters, endpoint);
            var requestContext = new DynamicEndpointRequestContext(context, endpoint.Metadata);
            foreach (var filter in filters)
            {
                await filter.OnRequestAsync(requestContext);
                if (requestContext.Result is { } shortCircuit)
                {
                    await shortCircuit.ExecuteAsync(context);
                    return Outcomes.ShortCircuited;
                }
            }
        }

        BindingResult binding;
        using (var bindingActivity = DynamicEndpointsInstrumentation.StartActivity(Activities.Binding, endpoint))
        {
            binding = await binder.BindAsync(context, endpoint);
            bindingActivity?.SetTag(Tags.ValidationErrors, binding.Errors.Count);
        }

        if (binding.Rejection is { } rejection)
        {
            instrumentation.RecordValidationFailure(endpoint, ValidationLayers.Binding);
            await RejectAsync(context, endpoint, filters, binding, rejection);
            return Outcomes.Rejected;
        }

        // Cheap checks first, all reported together; expensive request-level validators only for otherwise valid input.
        var errors = binding.Errors;
        var failedLayers = new List<string>(1);
        if (errors.HasErrors)
        {
            failedLayers.Add(ValidationLayers.Binding);
        }

        await ValidateLayerAsync(ValidationLayers.Constraints, endpoint, errors, failedLayers, () =>
        {
            RequestValidator.ValidateStructure(endpoint, binding.Values, errors);
            return Task.CompletedTask;
        });

        if (endpoint.Parameters.Any(p => p.Validators.Count > 0))
        {
            await ValidateLayerAsync(ValidationLayers.ParameterValidators, endpoint, errors, failedLayers,
                () => RunParameterValidatorsAsync(context, endpoint, binding, errors, items));
        }

        if (!errors.HasErrors && endpoint.Rules.Count > 0)
        {
            await ValidateLayerAsync(ValidationLayers.Rules, endpoint, errors, failedLayers, () =>
            {
                RequestValidator.ValidateRules(endpoint, binding.Values, errors);
                return Task.CompletedTask;
            });
        }

        if (!errors.HasErrors && endpoint.Validators.Count > 0)
        {
            await ValidateLayerAsync(ValidationLayers.RequestValidators, endpoint, errors, failedLayers, async () =>
            {
                foreach (var validator in endpoint.Validators)
                {
                    await RunAsync(context, endpoint, validator, null, binding, errors, items);
                }
            });
        }

        if (errors.HasErrors)
        {
            logger.LogDebug("Request to dynamic endpoint {Method} {Route} failed validation.", definition.Method, definition.Route);
            foreach (var layer in failedLayers)
            {
                instrumentation.RecordValidationFailure(endpoint, layer);
            }

            var title = errors.Format(ErrorMessage.Of("title.validation", string.Empty));
            await RejectAsync(context, endpoint, filters, binding,
                new RequestRejection(DynamicRequestRejection.Validation, StatusCodes.Status400BadRequest, title));
            return Outcomes.Rejected;
        }

        var started = Stopwatch.GetTimestamp();
        using (var processorActivity = DynamicEndpointsInstrumentation.StartActivity(Activities.Processor, endpoint))
        {
            try
            {
                var processor = context.RequestServices.GetRequiredKeyedService<IDynamicEndpointProcessor>(endpoint.ProcessorName);
                var tenant = definition.Tenant ?? (options.Value.Tenancy.Enabled ? await tenants.ResolveAsync(context) : null);
                var result = await processor.ProcessAsync(new DynamicRequest(endpoint, binding.Values, binding.Files, context, items, tenant));
                await ResponseCaching.ExecuteAsync(context, endpoint, result ?? Results.Empty);
                processorActivity?.SetTag(Tags.StatusCode, context.Response.StatusCode);
            }
            catch (Exception ex)
            {
                DynamicEndpointsInstrumentation.Failed(processorActivity, ex);
                throw;
            }
            finally
            {
                instrumentation.RecordProcessor(endpoint, Stopwatch.GetElapsedTime(started));
            }
        }

        return Outcomes.Processed;
    }

    private static async ValueTask ValidateLayerAsync(
        string layer, CompiledEndpoint endpoint, ValidationErrors errors, List<string> failedLayers, Func<Task> validate)
    {
        var activity = DynamicEndpointsInstrumentation.StartValidation(layer, endpoint);
        var before = errors.Count;
        try
        {
            await validate();
        }
        catch (Exception ex)
        {
            DynamicEndpointsInstrumentation.Failed(activity, ex);
            throw;
        }
        finally
        {
            DynamicEndpointsInstrumentation.StopValidation(activity, errors.Count - before);
        }

        if (errors.Count > before)
        {
            failedLayers.Add(layer);
        }
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
