namespace DynamicEndpoints.Samples.CustomProcessors;

/// <summary>
/// A filter runs for every dynamic endpoint, resolved from the request scope (inject what you need). This one:
/// <list type="bullet">
/// <item>before validation: every <c>DELETE</c> endpoint needs the API key of the configuration (<c>Samples:ApiKey</c>) in
/// <c>X-Api-Key</c> – a short-circuit with 401;</item>
/// <item>on rejected requests: logs them and adds the number of errors as a response header.</item>
/// </list>
/// Registered with <c>AddFilter&lt;ApiKeyFilter&gt;()</c>.
/// </summary>
public sealed class ApiKeyFilter(IConfiguration configuration, ILogger<ApiKeyFilter> logger) : IDynamicEndpointFilter
{
    public ValueTask OnRequestAsync(DynamicEndpointRequestContext context)
    {
        var apiKey = configuration["Samples:ApiKey"];
        if (HttpMethods.IsDelete(context.Endpoint.Definition.Method) &&
            (string.IsNullOrEmpty(apiKey) || context.HttpContext.Request.Headers["X-Api-Key"] != apiKey))
        {
            // Setting a result answers the request: no body is read, nothing is validated, the processor doesn't run.
            context.Result = Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "X-Api-Key is missing or wrong.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context)
    {
        logger.LogInformation("{Endpoint} rejected a request ({Reason}): {Errors}",
            context.Endpoint.Name, context.Reason, string.Join("; ", context.Errors.Select(e => $"{e.Key}: {e.Code}")));
        context.HttpContext.Response.Headers["X-Validation-Errors"] = context.Errors.Count.ToString();
        return ValueTask.CompletedTask;   // context.Result stays the default response
    }
}
