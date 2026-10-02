# File uploads & forms

`Form` parameters read `multipart/form-data` or `application/x-www-form-urlencoded` bodies. Text fields are converted like query
parameters. Files are streamed by ASP.NET Core (to disk above a small threshold), so there is no base64 and no double buffering.

```csharp
DynamicEndpoint.Post("/documents")
    .HandledBy<DocumentProcessor>()
    .FromForm("title", p => p.Required().MaxLength(100))
    .FromForm("document", p => p.File(maxSize: 10 * 1024 * 1024, "application/pdf", "image/*").Required())
    .FromForm("attachments", p => p.Files(maxSize: 1024 * 1024).Items(0, 5));

// in the processor (or a validator)
var file = request.GetFile("document");                 // IFormFile
await using var stream = file!.OpenReadStream();
```

- In `request.Parameters` (and in JsonLogic rules) a file is its metadata: `{ "fileName", "contentType", "length" }`.
- `AllowedContentTypes` is checked against the `Content-Type` the client sent. Inspect the content when it matters.
- An endpoint reads either a JSON body or a form, not both. The overall form size limit is `MaxFormBodySize` (30 MB).
- OpenAPI documents the body as `multipart/form-data` with `format: binary` file fields, so Swagger UI shows a file picker.
