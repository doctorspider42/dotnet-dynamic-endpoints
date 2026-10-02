namespace DynamicEndpoints.Sql;

/// <summary>
/// Lexical checks of a query before it is saved: a single <c>SELECT</c>/<c>WITH</c> statement without data-changing keywords.
/// Standard string literals, double-quoted identifiers and comments are skipped, so <c>'DROP'</c> in a string is fine. Everything
/// dialect-specific (backslash escapes, dollar quoting, brackets, backticks, MySQL <c>#</c> comments) is read as code – dialects only
/// ever make a literal longer than the standard one, so nothing a database executes is skipped (at worst a harmless query is
/// rejected). This is a safety net, not a sandbox: functions with side effects can't be detected lexically. Use a read-only
/// database user.
/// </summary>
internal static class SqlStatementGuard
{
    private static readonly HashSet<string> AllowedFirstWords = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WITH", "VALUES" };

    private static readonly HashSet<string> ForbiddenWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "DROP", "ALTER", "CREATE", "TRUNCATE", "RENAME", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "CALL", "DO", "COPY", "ATTACH", "DETACH", "PRAGMA", "INTO", "LOCK", "UNLOCK", "VACUUM", "ANALYZE", "REINDEX",
        "CLUSTER", "SET", "RESET", "DECLARE", "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "LOAD", "HANDLER", "OUTFILE",
        "DUMPFILE", "SHUTDOWN", "KILL", "BACKUP", "RESTORE", "DBCC", "OPENROWSET", "OPENQUERY", "OPENDATASOURCE", "BULK", "NOTIFY",
        "LISTEN", "PREPARE", "DEALLOCATE", "REFRESH", "IMPORT", "REPLACE",
    };

    /// <param name="Errors">Why the query can't be used; empty when it passed.</param>
    /// <param name="Parameters">Placeholder names in order of appearance, without the prefix.</param>
    /// <param name="Statement">The query without a trailing semicolon.</param>
    public sealed record Result(IReadOnlyList<string> Errors, IReadOnlyList<string> Parameters, string Statement);

    public static Result Check(string sql, char parameterPrefix)
    {
        var errors = new List<string>();
        var parameters = new List<string>();
        var words = new List<string>();
        var end = sql.Length;
        var afterSemicolon = false;

        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (afterSemicolon && !(c == '-' && IsLineComment(sql, i)) && !(c == '/' && Peek(sql, i + 1) == '*'))
            {
                errors.Add("Only a single statement is allowed.");
                afterSemicolon = false;
            }

            if (c == '-' && IsLineComment(sql, i))
            {
                i = Skip(sql, i, "\n");
            }
            else if (c == '/' && Peek(sql, i + 1) == '*')
            {
                if (Peek(sql, i + 2) is '!' or '+')
                {
                    errors.Add("Executable comments and optimizer hints (/*! … */, /*+ … */) are not allowed.");
                }

                i = Skip(sql, i + 2, "*/");
            }
            else if (c is '\'' or '"')
            {
                i = SkipQuoted(sql, i, c);
            }
            else if (c == parameterPrefix && IsIdentifierStart(Peek(sql, i + 1)) && Peek(sql, i - 1) != parameterPrefix)
            {
                var start = ++i;
                while (i < sql.Length && IsIdentifierPart(sql[i]))
                {
                    i++;
                }

                var name = sql[start..i];
                if (!parameters.Contains(name, StringComparer.Ordinal))
                {
                    parameters.Add(name);
                }
            }
            else if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < sql.Length && IsIdentifierPart(sql[i]))
                {
                    i++;
                }

                words.Add(sql[start..i]);
            }
            else if (c == ';')
            {
                if (end == sql.Length)
                {
                    end = i;
                }

                afterSemicolon = true;
                i++;
            }
            else
            {
                i++;
            }
        }

        if (words.Count == 0)
        {
            errors.Insert(0, "The query is empty.");
        }
        else if (!AllowedFirstWords.Contains(words[0]))
        {
            errors.Insert(0, "Only SELECT queries (SELECT, WITH … SELECT, VALUES) are allowed.");
        }

        foreach (var word in words.Where(ForbiddenWords.Contains).Select(w => w.ToUpperInvariant()).Distinct())
        {
            errors.Add($"'{word}' is not allowed in a read-only query.");
        }

        return new Result(errors.Distinct().ToList(), parameters, sql[..end].TrimEnd());
    }

    // MySQL needs whitespace after "--", so "--x" is read as code everywhere.
    private static bool IsLineComment(string sql, int i) =>
        Peek(sql, i + 1) == '-' && (i + 2 == sql.Length || char.IsWhiteSpace(sql[i + 2]));

    private static char Peek(string sql, int i) => i >= 0 && i < sql.Length ? sql[i] : '\0';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';

    // Index after the terminator, or the end of the text when it is missing.
    private static int Skip(string sql, int from, string terminator)
    {
        var at = sql.IndexOf(terminator, from, StringComparison.Ordinal);
        return at < 0 ? sql.Length : at + terminator.Length;
    }

    // Standard SQL: a doubled quote escapes the quote character, backslashes are plain characters.
    private static int SkipQuoted(string sql, int start, char quote)
    {
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] != quote)
            {
                continue;
            }

            if (Peek(sql, i + 1) != quote)
            {
                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }
}
