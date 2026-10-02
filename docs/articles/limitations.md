# Limitations (deliberate)

- `MapDynamicEndpoints()` must be called on the application, not inside a `MapGroup`.
- Conflict detection compares route *shapes*. It doesn't analyse constraints or optional segments.
- Query arrays use repeated keys (`?tag=a&tag=b`). Header arrays are comma separated.
- `DateTime` values must be RFC 3339 with an offset (`2026-01-31T12:00:00Z`).
