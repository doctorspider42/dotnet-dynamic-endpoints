namespace DynamicEndpoints.Runtime;

internal static class DefinitionNormalizer
{
    public static DynamicEndpointDefinition Normalize(DynamicEndpointDefinition definition)
    {
        var d = DynamicEndpointsJson.DeepClone(definition);
        var route = (d.Route ?? string.Empty).Trim();
        if (route.Length > 0 && route[0] != '/')
        {
            route = "/" + route;
        }

        if (route.Length > 1)
        {
            route = route.TrimEnd('/');
        }

        return d with
        {
            Method = (d.Method ?? string.Empty).Trim().ToUpperInvariant(),
            Route = route,
            Name = Clean(d.Name),
            Description = Clean(d.Description),
            Processor = Clean(d.Processor),
            AuthorizationPolicy = Clean(d.AuthorizationPolicy),
            RateLimitingPolicy = Clean(d.RateLimitingPolicy),
            Group = Clean(d.Group),
            Caching = d.Caching is { } caching
                ? caching with
                {
                    OutputCachePolicy = Clean(caching.OutputCachePolicy),
                    VaryByQuery = CleanList(caching.VaryByQuery),
                    VaryByHeader = CleanList(caching.VaryByHeader),
                }
                : null,
            RateLimit = d.RateLimit is { } rateLimit ? rateLimit with { PartitionHeader = Clean(rateLimit.PartitionHeader) } : null,
            Tenant = Clean(d.Tenant),
            Parameters = (d.Parameters ?? []).Select(p => p with
            {
                Name = (p.Name ?? string.Empty).Trim(),
                SourceName = Clean(p.SourceName) is { } s && !string.Equals(s, p.Name?.Trim(), StringComparison.Ordinal) ? s : null,
                Description = Clean(p.Description),
                Pattern = string.IsNullOrEmpty(p.Pattern) ? null : p.Pattern,
                AllowedValues = p.AllowedValues is { Count: > 0 } ? p.AllowedValues : null,
                AllowedContentTypes = p.AllowedContentTypes?.Select(c => (c ?? string.Empty).Trim()).Where(c => c.Length > 0).ToList() is { Count: > 0 } types
                    ? types
                    : null,
                Validators = p.Validators is { Count: > 0 } ? NormalizeValidators(p.Validators) : null,
            }).ToList(),
            Validators = NormalizeValidators(d.Validators),
            Rules = (d.Rules ?? []).Select(r => r with
            {
                Name = Clean(r.Name),
                Message = (r.Message ?? string.Empty).Trim(),
                Code = Clean(r.Code),
                Parameter = Clean(r.Parameter),
            }).ToList(),
        };
    }

    private static List<ValidatorReference> NormalizeValidators(IReadOnlyList<ValidatorReference>? validators) =>
        (validators ?? []).Select(v => v with { Name = (v.Name ?? string.Empty).Trim() }).ToList();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string>? CleanList(IReadOnlyList<string>? values) =>
        values?.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } list ? list : null;
}
