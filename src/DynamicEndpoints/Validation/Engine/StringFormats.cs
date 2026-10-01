using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DynamicEndpoints.Validation.Engine;

/// <summary>String formats understood by the schema validator (JSON Schema <c>format</c> keyword).</summary>
internal static partial class StringFormats
{
    private static readonly Dictionary<string, (Func<string, bool> IsValid, string Description)> Formats = new(StringComparer.Ordinal)
    {
        ["date"] = (IsDate, "a date in the format YYYY-MM-DD"),
        ["date-time"] = (IsDateTime, "an RFC 3339 date-time with an offset, e.g. 2026-01-31T12:00:00Z"),
        ["time"] = (IsTime, "a time in the format HH:mm or HH:mm:ss"),
        ["uuid"] = (IsUuid, "a UUID"),
        ["email"] = (IsEmail, "a valid e-mail address"),
        ["uri"] = (IsUri, "an absolute URI, e.g. https://example.com"),
        ["phone"] = (IsPhone, "a phone number in E.164 format, e.g. +48123456789"),
        ["ipv4"] = (IsIpv4, "an IPv4 address"),
        ["ipv6"] = (IsIpv6, "an IPv6 address"),
    };

    public static IEnumerable<string> Names => Formats.Keys;

    public static bool IsKnown(string format) => Formats.ContainsKey(format);

    /// <returns><c>null</c> when valid (or the format is unknown), otherwise an error message.</returns>
    public static string? Check(string format, string value) =>
        Formats.TryGetValue(format, out var f) && !f.IsValid(value) ? $"Must be {f.Description}." : null;

    public static string ToSchemaName(ParameterFormat format) => format switch
    {
        ParameterFormat.Email => "email",
        ParameterFormat.Uri => "uri",
        ParameterFormat.Phone => "phone",
        ParameterFormat.Ipv4 => "ipv4",
        ParameterFormat.Ipv6 => "ipv6",
        ParameterFormat.Time => "time",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static bool IsDate(string s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsDateTime(string s) =>
        DateTimeRegex().IsMatch(s) &&
        DateTimeOffset.TryParse(s.Replace(' ', 'T'), CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsTime(string s) =>
        TimeOnly.TryParseExact(s, ["HH:mm", "HH:mm:ss", "HH:mm:ss.FFFFFFF"], CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsUuid(string s) => Guid.TryParseExact(s, "D", out _);

    private static bool IsPhone(string s) => PhoneRegex().IsMatch(s);

    private static bool IsUri(string s) =>
        UriSchemeRegex().IsMatch(s) && !s.Any(char.IsWhiteSpace) && Uri.TryCreate(s, UriKind.Absolute, out _);

    private static bool IsIpv4(string s)
    {
        var parts = s.Split('.');
        return parts.Length == 4 && parts.All(p =>
            p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && (p.Length == 1 || p[0] != '0') && int.Parse(p, CultureInfo.InvariantCulture) <= 255);
    }

    private static bool IsIpv6(string s) =>
        s.Contains(':') && !s.Contains('%') &&
        IPAddress.TryParse(s, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;

    // Pragmatic check (no comments, no quoted local parts, no IP literals) – what real-world forms accept.
    private static bool IsEmail(string s)
    {
        if (s.Length > 254 || s.Any(char.IsWhiteSpace))
        {
            return false;
        }

        var at = s.IndexOf('@');
        if (at <= 0 || at != s.LastIndexOf('@') || at == s.Length - 1)
        {
            return false;
        }

        var local = s[..at];
        var domain = s[(at + 1)..];
        if (local.Length > 64 || local[0] == '.' || local[^1] == '.' || local.Contains("..") ||
            !local.All(c => char.IsLetterOrDigit(c) || "!#$%&'*+/=?^_`{|}~.-".Contains(c)))
        {
            return false;
        }

        var labels = domain.Split('.');
        return labels.Length >= 2 && labels.All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' &&
            label.All(c => char.IsLetterOrDigit(c) || c == '-')) && !labels[^1].All(char.IsAsciiDigit);
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt ]\d{2}:\d{2}:\d{2}(\.\d+)?([Zz]|[+-]\d{2}:\d{2})$")]
    private static partial Regex DateTimeRegex();

    [GeneratedRegex(@"^\+[1-9][0-9]{6,14}$")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]*:")]
    private static partial Regex UriSchemeRegex();
}
