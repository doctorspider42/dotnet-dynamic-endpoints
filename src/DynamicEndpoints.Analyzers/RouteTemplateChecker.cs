using System.Collections.Generic;
using System.Text;

namespace DynamicEndpoints.Analyzers;

/// <summary>
/// A lightweight re-implementation of the checks of ASP.NET Core's <c>RoutePatternFactory.Parse</c> (the analyzer cannot
/// reference ASP.NET Core). Deliberately conservative: it only reports templates the real parser rejects.
/// </summary>
internal static class RouteTemplateChecker
{
    private sealed class Part
    {
        public bool IsParameter;
        public string Text = "";
        public string Name = "";
        public bool CatchAll;
        public bool Optional;
    }

    /// <returns>The first problem found, or <c>null</c> for a valid template.</returns>
    public static string? Check(string template)
    {
        if (template.Length == 0 || template == "/")
        {
            return "a route is required and cannot be the application root";
        }

        var i = 0;
        if (template.StartsWith("~/", System.StringComparison.Ordinal))
        {
            i = 2;
        }
        else if (template[0] == '~')
        {
            return "a route cannot start with '~' unless it is followed by '/'";
        }
        else if (template[0] == '/')
        {
            i = 1;
        }

        var segments = new List<List<Part>>();
        var current = new List<Part>();
        var literal = new StringBuilder();

        void FlushLiteral()
        {
            if (literal.Length > 0)
            {
                current.Add(new Part { Text = literal.ToString() });
                literal.Clear();
            }
        }

        while (i < template.Length)
        {
            var c = template[i];
            if (c == '/')
            {
                FlushLiteral();
                if (current.Count == 0)
                {
                    return "the separator '/' cannot appear twice in a row";
                }

                segments.Add(current);
                current = [];
                i++;
            }
            else if (c == '{' && Next(template, i) == '{' || c == '}' && Next(template, i) == '}')
            {
                literal.Append(c); // escaped brace
                i += 2;
            }
            else if (c == '}')
            {
                return "'}' has no matching '{' (write '}}' for a literal brace)";
            }
            else if (c == '?')
            {
                return "literal sections cannot contain '?' (query strings are not part of a route)";
            }
            else if (c == '{')
            {
                FlushLiteral();
                var content = new StringBuilder();
                var j = i + 1;
                while (true)
                {
                    if (j >= template.Length)
                    {
                        return "a '{' has no matching '}'";
                    }

                    var p = template[j];
                    if (p == '{' || p == '}')
                    {
                        if (Next(template, j) == p)
                        {
                            content.Append(p); // escaped brace inside the parameter, e.g. regex(^\d{{3}}$)
                            j += 2;
                            continue;
                        }

                        if (p == '{')
                        {
                            return "'{' and '}' inside a route parameter must be escaped as '{{' and '}}'";
                        }

                        break;
                    }

                    content.Append(p);
                    j++;
                }

                var parameter = ParseParameter(content.ToString(), out var problem);
                if (problem is not null)
                {
                    return problem;
                }

                current.Add(parameter);
                i = j + 1;
            }
            else
            {
                literal.Append(c);
                i++;
            }
        }

        FlushLiteral();
        segments.Add(current); // may be empty for a trailing '/', which is allowed

        return CheckSegments(segments);
    }

    private static char Next(string text, int i) => i + 1 < text.Length ? text[i + 1] : '\0';

    private static Part ParseParameter(string content, out string? problem)
    {
        problem = null;
        var part = new Part { IsParameter = true, Text = "{" + content + "}" };
        var rest = content;
        if (rest.StartsWith("**", System.StringComparison.Ordinal))
        {
            part.CatchAll = true;
            rest = rest.Substring(2);
        }
        else if (rest.StartsWith("*", System.StringComparison.Ordinal))
        {
            part.CatchAll = true;
            rest = rest.Substring(1);
        }

        if (rest.EndsWith("?", System.StringComparison.Ordinal))
        {
            part.Optional = true;
            rest = rest.Substring(0, rest.Length - 1);
        }

        // The name ends at the first constraint (':') or default value ('=').
        var end = rest.IndexOfAny([':', '=']);
        part.Name = end < 0 ? rest : rest.Substring(0, end);

        if (end < 0 && part.Name.Length == 0)
        {
            problem = $"the parameter '{part.Text}' has no name";
        }
        else if (part.Name.IndexOfAny(['{', '}', '/', '?', '*']) >= 0)
        {
            problem = $"the parameter name '{part.Name}' is invalid; names cannot contain '{{', '}}', '/', '?' or '*' ('*' may only start a catch-all, '?' may only end an optional parameter)";
        }
        else if (part.CatchAll && part.Optional)
        {
            problem = $"the catch-all parameter '{part.Name}' cannot be marked optional";
        }
        else if (part.Optional && end >= 0 && rest[end] == '=')
        {
            problem = $"the optional parameter '{part.Name}' cannot have a default value";
        }

        return part;
    }

    private static string? CheckSegments(List<List<Part>> segments)
    {
        var names = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        var lastNonEmpty = segments.FindLastIndex(s => s.Count > 0);
        for (var s = 0; s < segments.Count; s++)
        {
            var parts = segments[s];
            for (var k = 0; k < parts.Count; k++)
            {
                var part = parts[k];
                if (!part.IsParameter)
                {
                    continue;
                }

                if (part.Name.Length > 0 && !names.Add(part.Name))
                {
                    return $"the parameter '{part.Name}' appears more than once";
                }

                if (part.CatchAll && parts.Count > 1)
                {
                    return $"the catch-all parameter '{part.Name}' must be the only content of its segment";
                }

                if (part.CatchAll && s != lastNonEmpty)
                {
                    return $"the catch-all parameter '{part.Name}' can only appear in the last segment";
                }

                if (k + 1 < parts.Count && parts[k + 1].IsParameter)
                {
                    return "a segment cannot contain two consecutive parameters; separate them with '/' or a literal";
                }

                if (part.Optional && parts.Count > 1)
                {
                    if (k + 1 < parts.Count)
                    {
                        return $"the optional parameter '{part.Name}' must be at the end of its segment";
                    }

                    if (k > 0 && !parts[k - 1].IsParameter && parts[k - 1].Text != ".")
                    {
                        return $"only a period (.) can precede the optional parameter '{part.Name}' in a segment";
                    }
                }
            }
        }

        return null;
    }
}
