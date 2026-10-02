# Limitations (deliberate)

- `MapDynamicEndpoints()` must be called on the application, not inside a `MapGroup`.
- Conflict detection compares route *shapes*. It doesn't analyse constraints or optional segments.
- Query arrays use repeated keys (`?tag=a&tag=b`). Header arrays are comma separated.
- `DateTime` values must be RFC 3339 with an offset (`2026-01-31T12:00:00Z`).
- Rate limit counters live in the memory of each instance, so N instances allow up to N times the limit.
- Webhooks with `background: true` are queued in memory: deliveries still pending at shutdown are lost.
- An import writes endpoint by endpoint, not in one transaction (it is validated as a whole first).
- History and drafts need a store implementing `IDynamicEndpointRevisionStore` (the in-memory and EF Core stores do).
