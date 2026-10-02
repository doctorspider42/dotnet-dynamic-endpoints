namespace DynamicEndpoints.Samples.Showcase.Greetings;

/// <summary>
/// A purpose-built management API (instead of / next to <c>MapDynamicEndpointsAdmin</c>):
/// POST /api/greetings { "slug": "pirate", "greeting": "Ahoy" }  →  GET /greetings/pirate/{name} exists immediately.
/// </summary>
public static class GreetingsApi
{
    public static RouteGroupBuilder MapGreetingsApi(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/greetings").WithTags("Greetings (custom management API)");

        group.MapGet("/", (GreetingEndpointsService greetings, CancellationToken ct) => greetings.ListAsync(ct));

        group.MapPost("/", async (GreetingRequest request, GreetingEndpointsService greetings, CancellationToken ct) =>
        {
            try
            {
                var created = await greetings.CreateAsync(request, ct);
                return Results.Created(created.Url.Replace("{name}", "World"), created);
            }
            catch (DynamicEndpointValidationException ex)
            {
                // Also covers library-level problems, e.g. "Route conflicts with dynamic endpoint …" for a taken slug.
                return Results.ValidationProblem(ex.Errors);
            }
        });

        group.MapDelete("/{slug}", async (string slug, GreetingEndpointsService greetings, CancellationToken ct) =>
            await greetings.DeleteAsync(slug, ct) ? Results.NoContent() : Results.NotFound());

        return group;
    }
}
