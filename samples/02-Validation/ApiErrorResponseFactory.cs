namespace DynamicEndpoints.Samples.Validation;

/// <summary>
/// One error format for every error: validation errors (400), malformed bodies, 413 and 415 – and, with
/// <c>app.UseDynamicEndpointsErrorResponses()</c>, empty 404/405 responses and unhandled exceptions too. Registered with
/// <c>UseErrorResponseFactory&lt;ApiErrorResponseFactory&gt;()</c>.
/// </summary>
/// <example>
/// <code>
/// { "error": { "code": "Validation", "message": "One or more validation errors occurred.",
///              "details": [{ "field": "email", "code": "format", "message": "Must be a valid e-mail address." }] },
///   "requestId": "4bf92f3577b34da6a3ce929d0e0e4736" }
/// </code>
/// </example>
public sealed class ApiErrorResponseFactory : IDynamicErrorResponseFactory
{
    public IResult CreateResponse(DynamicErrorContext context) => Results.Json(new
    {
        error = new
        {
            code = context.Kind.ToString(),                 // Validation, InvalidBody, NotFound, MethodNotAllowed, Exception, …
            message = context.Detail ?? context.Title,      // localized like the validation messages
            details = context.Errors.Select(e => new { field = e.Key, code = e.Code, message = e.Message }),
        },
        requestId = context.RequestId,                      // the trace id – find the request in your logs
    }, statusCode: context.StatusCode);
}
