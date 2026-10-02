# One error format: `IDynamicErrorResponseFactory`

One factory builds every error: validation errors, malformed bodies, 413 and 415. With the middleware it also covers empty 401/403
responses from authentication and authorization, 404 for unknown routes, 405, empty errors returned by processors, and unhandled
exceptions. So `UseStatusCodePages` isn't needed any more.

```csharp
builder.Services.AddDynamicEndpoints().UseErrorResponses(e => Results.Json(new
{
    apiVersion = "1.0",
    error = new { code = e.Kind.ToString(), message = e.Title,                 // Validation, NotFound, Unauthorized, Exception, …
                  details = e.Errors.Select(x => new { x.Key, x.Code, x.Message }) },
    requestId = e.RequestId,                                                     // trace id
}, statusCode: e.StatusCode));                                                   // or UseErrorResponseFactory<MyFactory>()

app.UseDynamicEndpointsErrorResponses(o => o.AppliesTo = c => c.Request.Path.StartsWithSegments("/api"));   // early in the pipeline
```

- Responses that already have a body are left alone, and `[SkipStatusCodePages]` is respected.
- Exceptions are logged and answered with a 500. `e.Exception` is there for you, but never in the default response. To let
  `UseExceptionHandler` handle them instead, set `o.HandleExceptions = false`.
- Titles are localized like the validation messages. `e.Endpoint` is the dynamic endpoint (`null` for unknown routes).
- `IDynamicEndpointFilter.OnValidationFailedAsync` still runs afterwards and can replace the result of a single request.
