# OpenAPI: security schemes, common headers, examples, hooks

```csharp
builder.Services.AddDynamicEndpoints(o =>
{
    o.OpenApi.AddApiKey("X-Api-Key");                                    // securitySchemes + a requirement on every non-anonymous operation
    o.OpenApi.AddSecurityScheme("Bearer", bearerScheme, appliesTo: d => d.Group == "partners");
    o.OpenApi.AddHeader("X-End-User", "End user the call is made for.", required: true);
    o.OpenApi.AddHeader("X-Seat-Id", "Seat of the end user.");
    o.OpenApi.AddHeader("Idempotency-Key", "Makes retries safe.", appliesTo: d => d.Method != "GET",
        schema: new JsonObject { ["type"] = "string", ["format"] = "uuid" }, example: "6f9619ff-8b86-d011-b42d-00cf4fc964ff");
    o.OpenApi.ConfigureOperation = (operation, definition) => { /* x-extensions, extra responses */ };
    o.OpenApi.ConfigureDocument = document => { /* servers, your error schema */ };
});

DynamicEndpoint.Post("/orders")
    .FromBody("sku", p => p.Required().Example("A-1"))       // examples compose the request example…
    .WithRequestExample(new { sku = "A-1", quantity = 2 })   // …or set it by hand
    .WithResponseExample(new { id = 7, status = "accepted" });
```

- **Headers** are real header parameters of each operation, so client generators produce arguments for them.
- **Security requirements** are attached to each operation. Endpoints with `AllowAnonymous` get none.
- **Examples:** parameter examples show up on query, header and route parameters and in the request body example.
