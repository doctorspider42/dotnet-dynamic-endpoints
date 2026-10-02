using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace DynamicEndpoints.Runtime;

internal static class RouteKeys
{
    /// <summary>
    /// Shape of a route with parameter names and constraints erased, so <c>/orders/{id}</c> and
    /// <c>/Orders/{orderId:int}</c> produce the same key and are reported as ambiguous.
    /// </summary>
    public static string Normalize(RoutePattern pattern) => Render(pattern, p => p.IsCatchAll ? "{**}" : "{}", lowerCase: true);

    /// <summary>OpenAPI path template: <c>/orders/{id:int}</c> -> <c>/orders/{id}</c>.</summary>
    public static string ToOpenApiPath(RoutePattern pattern) => Render(pattern, p => "{" + p.Name + "}", lowerCase: false);

    /// <summary>OpenAPI path template with the parameter <paramref name="name"/> replaced by a literal value.</summary>
    public static string ToOpenApiPath(RoutePattern pattern, string name, string value) =>
        Render(pattern, p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ? value : "{" + p.Name + "}", lowerCase: false);

    private static string Render(RoutePattern pattern, Func<RoutePatternParameterPart, string> parameter, bool lowerCase)
    {
        var builder = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            builder.Append('/');
            foreach (var part in segment.Parts)
            {
                builder.Append(part switch
                {
                    RoutePatternLiteralPart literal => lowerCase ? literal.Content.ToLowerInvariant() : literal.Content,
                    RoutePatternSeparatorPart separator => separator.Content,
                    RoutePatternParameterPart p => parameter(p),
                    _ => string.Empty,
                });
            }
        }

        return builder.Length == 0 ? "/" : builder.ToString();
    }
}
