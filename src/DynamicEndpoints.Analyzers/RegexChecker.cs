using System;
using System.Text.RegularExpressions;

namespace DynamicEndpoints.Analyzers;

/// <summary>Checks parameter patterns the way the endpoint compiler does (<c>RegexOptions.NonBacktracking</c>).</summary>
internal static class RegexChecker
{
    /// <summary>Parses the pattern with the regular engine; returns the parser's message for an invalid pattern.</summary>
    public static string? FindSyntaxError(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.CultureInvariant);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// Finds the first construct <c>NonBacktracking</c> rejects (netstandard2.0 has no such option to try it directly).
    /// Escaped characters and character classes are skipped, so <c>\(?=</c> or <c>[\1]</c> are not reported.
    /// </summary>
    public static string? FindUnsupportedConstruct(string pattern)
    {
        var inClass = 0; // nesting depth of character classes (class subtraction: [a-z-[aeiou]])
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    return null;
                }

                var next = pattern[i + 1];
                if (inClass == 0)
                {
                    if (next is >= '1' and <= '9')
                    {
                        return "a backreference (\\" + next + ")";
                    }

                    if (next == 'k' && i + 2 < pattern.Length && pattern[i + 2] is '<' or '\'')
                    {
                        return "a named backreference (\\k<name>)";
                    }

                    if (next == 'G')
                    {
                        return "the contiguous-match anchor (\\G)";
                    }
                }

                i++; // skip the escaped character
                continue;
            }

            if (inClass > 0)
            {
                if (c == '[' && pattern[i - 1] == '-')
                {
                    inClass++;
                }
                else if (c == ']' && !IsClassStart(pattern, i))
                {
                    inClass--;
                }

                continue;
            }

            if (c == '[')
            {
                inClass = 1;
                continue;
            }

            if (c != '(' || i + 1 >= pattern.Length || pattern[i + 1] != '?')
            {
                continue;
            }

            var rest = pattern.Substring(i + 2);
            if (rest.StartsWith("=", StringComparison.Ordinal) || rest.StartsWith("<=", StringComparison.Ordinal))
            {
                return "a positive lookaround ((?=…) / (?<=…))";
            }

            if (rest.StartsWith("!", StringComparison.Ordinal) || rest.StartsWith("<!", StringComparison.Ordinal))
            {
                return "a negative lookaround ((?!…) / (?<!…))";
            }

            if (rest.StartsWith(">", StringComparison.Ordinal))
            {
                return "an atomic group ((?>…))";
            }

            if (rest.StartsWith("(", StringComparison.Ordinal))
            {
                return "a conditional ((?(…)yes|no))";
            }

            if (rest.Length > 0 && rest[0] is '<' or '\'')
            {
                // (?<name>…) is fine, (?<open-close>…) is a balancing group
                var close = rest.IndexOf(rest[0] == '<' ? '>' : '\'', 1);
                if (close > 0 && rest.Substring(1, close - 1).IndexOf('-') >= 0)
                {
                    return "a balancing group ((?<open-close>…))";
                }
            }
        }

        return null;
    }

    // A ']' right after '[' or '[^' is a literal member of the class, not its end.
    private static bool IsClassStart(string pattern, int i) =>
        pattern[i - 1] == '[' && (i < 2 || pattern[i - 2] != '\\') ||
        pattern[i - 1] == '^' && i >= 2 && pattern[i - 2] == '[';
}
