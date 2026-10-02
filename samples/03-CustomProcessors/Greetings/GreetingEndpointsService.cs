using System.Text.RegularExpressions;
using DynamicEndpoints.Samples.CustomProcessors.Processors;

namespace DynamicEndpoints.Samples.CustomProcessors.Greetings;

public sealed record GreetingRequest(string Slug, string Greeting);

public sealed record GreetingEndpoint(Guid Id, string Slug, string Url, string Greeting, bool Enabled);

/// <summary>
/// Managing dynamic endpoints from your own code instead of the generic admin API:
/// <see cref="IDynamicEndpointManager"/> is constructor-injected like any other service.
/// The caller only provides business data – this service decides how the endpoint looks.
/// </summary>
public sealed partial class GreetingEndpointsService(IDynamicEndpointManager manager)
{
    private const string Group = "Greetings";
    private const string Prefix = "/greetings/";

    public async Task<IReadOnlyList<GreetingEndpoint>> ListAsync(CancellationToken cancellationToken)
    {
        var all = await manager.ListAsync(cancellationToken);
        return all.Where(s => s.Definition.Group == Group).Select(s => ToResponse(s.Definition)).ToList();
    }

    /// <exception cref="DynamicEndpointValidationException">Invalid slug/greeting or the slug is already taken.</exception>
    public async Task<GreetingEndpoint> CreateAsync(GreetingRequest request, CancellationToken cancellationToken)
    {
        // Business rules of this feature – the library validates the resulting definition on top of that.
        if (!SlugRegex().IsMatch(request.Slug ?? string.Empty))
        {
            throw new DynamicEndpointValidationException("slug", "Use 2-30 lowercase letters, digits or dashes.");
        }

        if (string.IsNullOrWhiteSpace(request.Greeting) || request.Greeting.Length > 100)
        {
            throw new DynamicEndpointValidationException("greeting", "Greeting is required (max 100 characters).");
        }

        var definition = DynamicEndpoint.Get(Prefix + request.Slug + "/{name}")
            .Named($"Greeting: {request.Slug}")
            .InGroup(Group)
            .HandledBy<TemplateProcessor, TemplateConfig>(new() { Template = request.Greeting + ", {{name}}!" })
            .FromRoute("name", p => p.String().Length(1, 50));

        // Validation, persistence and publishing happen here – the endpoint answers right after this call.
        var created = await manager.CreateAsync(definition, cancellationToken);
        return ToResponse(created);
    }

    public async Task<bool> DeleteAsync(string slug, CancellationToken cancellationToken)
    {
        var existing = (await manager.ListAsync(cancellationToken))
            .FirstOrDefault(s => s.Definition.Group == Group && SlugOf(s.Definition) == slug);
        return existing is not null && await manager.DeleteAsync(existing.Definition.Id, cancellationToken);
    }

    private static GreetingEndpoint ToResponse(DynamicEndpointDefinition d)
    {
        var greeting = d.ProcessorConfig?["template"]?.GetValue<string>() ?? string.Empty;
        return new GreetingEndpoint(d.Id, SlugOf(d), d.Route, greeting.Replace(", {{name}}!", string.Empty), d.Enabled);
    }

    [GeneratedRegex("^[a-z0-9-]{2,30}$")]
    private static partial Regex SlugRegex();

    private static string SlugOf(DynamicEndpointDefinition d) => d.Route[Prefix.Length..].Split('/')[0];
}
