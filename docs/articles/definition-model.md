# Definition model

| Area | What admins can set |
|---|---|
| Endpoint | method, route template (`/orders/{id}`), name, description, group (section in Swagger UI), enabled |
| Parameters | source (`Route`, `Query`, `Header`, `Body`, `Form`), name in request, type (`String`, `Integer`, `Number`, `Boolean`, `Date`, `DateTime`, `Guid`, `Array`, `Object`, `File`), required, default, example |
| Constraints | min/max length, minimum/maximum, regex pattern, allowed values, min/max items, custom JSON Schema for objects/arrays, max file size, allowed content types |
| Formats | `Email`, `Uri`, `Phone` (E.164), `Ipv4`, `Ipv6`, `Time` for strings; `Date`, `DateTime`, `Guid` as types |
| Rules | [JsonLogic](https://jsonlogic.com) conditions with an error message, an optional error code and a target parameter |
| Custom validators | code validators attached to parameters or to the whole request, with optional configuration |
| Processing | processor name and configuration (JSON), your own or a [built-in one](built-in-processors.md) |
| Security | allow anonymous, require authorization, authorization policy, rate limiting policy, or a rate limit and quota of its own ([rate limits](caching-and-rate-limits.md#rate-limits-and-quotas)) |
| Caching | `Cache-Control` (max age, public/private, no-store), ETags with `304`, server-side output caching ([caching](caching-and-rate-limits.md)) |
| Tenant | the tenant the endpoint belongs to, or none for shared endpoints ([multi-tenancy](multi-tenancy.md)) |
| Docs | response schema, request and response examples (documentation only) |
