# Filters: access checks, error format, metering

```csharp
builder.Services.AddDynamicEndpoints().AddFilter<TenantFeatureFilter>();   // scoped, run in registration order

public sealed class TenantFeatureFilter(ITenantFeatures features, IMeter meter) : IDynamicEndpointFilter
{
    // After routing and authorization, before the body is read or validated.
    public async ValueTask OnRequestAsync(DynamicEndpointRequestContext context)
    {
        if (!await features.HasAccessAsync(context.HttpContext, context.Endpoint.Definition.Group))
            context.Result = Results.Problem(statusCode: 403, title: "Feature not available");   // short-circuits
    }

    // Validation errors (400), malformed or undecodable bodies (400), 413 and 415.
    public ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context)
    {
        meter.Rejected(context.Endpoint.Id, context.Reason);
        context.Result = Results.Json(new
        {
            apiVersion = "1.0",
            error = new { code = "VALIDATION_FAILED", message = context.Title,
                          details = context.Errors.Select(e => new { e.Key, e.Code, e.Message }) },
        }, statusCode: context.StatusCode);
        return ValueTask.CompletedTask;
    }
}
```

Both methods are optional, and `AddFilter(onRequest: …, onValidationFailed: …)` registers one inline. `context.Result` starts
with the default problem response, so a filter that only logs leaves it alone. Custom validators can report codes too:
`context.AddError(path, message, code)`.

The routed endpoint carries the complete definition, so your own middleware doesn't need the store either:
`HttpContext.GetDynamicEndpoint()` gives `Definition`, `ProcessorName`, `Parameters`, `FindParameter`, `HasFiles` and friends.
