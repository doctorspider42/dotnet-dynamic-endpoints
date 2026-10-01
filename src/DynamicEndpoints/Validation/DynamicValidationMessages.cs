using System.Globalization;

namespace DynamicEndpoints;

/// <summary>A single request validation error.</summary>
/// <param name="Key">Request name of the offending value (<c>X-Tenant-Id</c>, <c>address.city</c>, <c>items[2]</c>) or <c>request</c>/<c>body</c>.</param>
/// <param name="Code">Stable, machine readable code – see <see cref="DynamicValidationCodes"/>.</param>
/// <param name="Message">Human readable, localized message.</param>
public sealed record DynamicValidationError(string Key, string Code, string Message);

/// <summary>Codes of the errors produced by the library. Custom validators default to <see cref="Custom"/>, rules to <see cref="Rule"/>.</summary>
public static class DynamicValidationCodes
{
    public const string Required = "required";
    public const string Type = "type";
    public const string MultipleValues = "multipleValues";
    public const string InvalidBody = "invalidBody";
    public const string NotAllowed = "notAllowed";
    public const string Const = "const";
    public const string Enum = "enum";
    public const string MinLength = "minLength";
    public const string MaxLength = "maxLength";
    public const string Minimum = "minimum";
    public const string Maximum = "maximum";
    public const string ExclusiveMinimum = "exclusiveMinimum";
    public const string ExclusiveMaximum = "exclusiveMaximum";
    public const string MultipleOf = "multipleOf";
    public const string UnknownField = "unknownField";
    public const string MinProperties = "minProperties";
    public const string MaxProperties = "maxProperties";
    public const string MinItems = "minItems";
    public const string MaxItems = "maxItems";
    public const string UniqueItems = "uniqueItems";
    public const string Format = "format";
    public const string Pattern = "pattern";
    public const string FileSize = "fileSize";
    public const string FileType = "fileType";
    public const string Rule = "rule";
    public const string Custom = "custom";
}

/// <summary>Input of <see cref="DynamicValidationMessages.Localizer"/>.</summary>
/// <param name="Key">Message key, e.g. <c>minLength</c>, <c>required.header</c> or <c>format.email</c> (see README for the full list).</param>
/// <param name="Code">Error code reported with the message.</param>
/// <param name="Arguments">Values for the <c>{0}</c>, <c>{1}</c>, … placeholders of the template.</param>
/// <param name="Count">The number plural forms depend on (lengths, item counts), when there is one.</param>
/// <param name="Culture">Culture the message is requested for.</param>
/// <param name="DefaultMessage">The message the library would use (built-in or <see cref="DynamicValidationMessages.Set"/> template).</param>
public sealed record DynamicValidationMessageContext(
    string Key,
    string Code,
    IReadOnlyList<object?> Arguments,
    long? Count,
    CultureInfo Culture,
    string DefaultMessage);

/// <summary>
/// Texts of request validation errors. English and Polish are built in; any message can be overridden and new languages added
/// with <see cref="Set"/>, or everything can be routed through your own localization with <see cref="Localizer"/>.
/// </summary>
/// <remarks>
/// Templates use <c>{0}</c>-style placeholders. Plural forms are looked up as <c>key#one</c>, <c>key#few</c>, <c>key#many</c>
/// and <c>key#other</c> (CLDR categories, English and Polish rules built in), falling back to <c>key</c>.
/// </remarks>
public sealed class DynamicValidationMessages
{
    private readonly Dictionary<string, Dictionary<string, string>> _templates = new(StringComparer.OrdinalIgnoreCase);

    public DynamicValidationMessages()
    {
        foreach (var (culture, templates) in BuiltInValidationMessages.All)
        {
            _templates[culture] = new Dictionary<string, string>(templates, StringComparer.Ordinal);
        }
    }

    /// <summary>Culture used when <see cref="UseRequestCulture"/> is off or the request culture has no messages. Default <c>en</c>.</summary>
    public string DefaultCulture { get; set; } = "en";

    /// <summary>
    /// Use <see cref="CultureInfo.CurrentUICulture"/> – set per request by <c>app.UseRequestLocalization()</c> – to pick the language.
    /// Off by default, so responses don't change with the server's regional settings.
    /// </summary>
    public bool UseRequestCulture { get; set; }

    /// <summary>Full control over the texts (e.g. an <c>IStringLocalizer</c>). Return <c>null</c> to keep the default message.</summary>
    public Func<DynamicValidationMessageContext, string?>? Localizer { get; set; }

    /// <summary>Cultures with built-in or added messages.</summary>
    public IReadOnlyCollection<string> Cultures => _templates.Keys;

    /// <summary>Message keys of the built-in English catalog.</summary>
    public static IReadOnlyCollection<string> Keys => [.. BuiltInValidationMessages.All["en"].Keys];

    /// <summary>Adds or replaces the template of <paramref name="key"/> for <paramref name="culture"/> (e.g. <c>"de"</c> or <c>"pl-PL"</c>).</summary>
    public DynamicValidationMessages Set(string culture, string key, string template)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(template);
        if (!_templates.TryGetValue(culture, out var templates))
        {
            _templates[culture] = templates = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        templates[key] = template;
        return this;
    }

    internal CultureInfo CurrentCulture =>
        UseRequestCulture ? CultureInfo.CurrentUICulture : CultureInfo.GetCultureInfo(DefaultCulture);

    internal string Format(ErrorMessage message) => Format(message, CurrentCulture);

    internal string Format(ErrorMessage message, CultureInfo culture)
    {
        var (template, templateCulture) = FindTemplate(message, culture);
        var text = template is null
            ? message.Key
            : string.Format(templateCulture, template, message.Args);

        return Localizer?.Invoke(new DynamicValidationMessageContext(message.Key, message.Code, message.Args, message.Count, culture, text)) ?? text;
    }

    private (string? Template, CultureInfo Culture) FindTemplate(ErrorMessage message, CultureInfo culture)
    {
        foreach (var candidate in Fallbacks(culture))
        {
            if (!_templates.TryGetValue(candidate.Name, out var templates))
            {
                continue;
            }

            if (message.Count is { } count &&
                templates.TryGetValue($"{message.Key}#{PluralCategory(candidate, count)}", out var plural))
            {
                return (plural, candidate);
            }

            if (templates.TryGetValue(message.Key, out var template))
            {
                return (template, candidate);
            }
        }

        return (null, CultureInfo.InvariantCulture);
    }

    private IEnumerable<CultureInfo> Fallbacks(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            yield return c;
        }

        var fallback = CultureInfo.GetCultureInfo(DefaultCulture);
        yield return fallback;
        if (!string.IsNullOrEmpty(fallback.Parent.Name))
        {
            yield return fallback.Parent;
        }

        yield return CultureInfo.GetCultureInfo("en");
    }

    // CLDR plural categories for integers: https://cldr.unicode.org/index/cldr-spec/plural-rules
    private static string PluralCategory(CultureInfo culture, long n)
    {
        var language = culture.TwoLetterISOLanguageName;
        switch (language)
        {
            case "pl":
                if (n == 1)
                {
                    return "one";
                }

                var mod10 = n % 10;
                var mod100 = n % 100;
                return mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few" : "many";
            case "ru" or "uk" or "be" or "hr" or "sr" or "bs":
                mod10 = n % 10;
                mod100 = n % 100;
                return mod10 == 1 && mod100 != 11 ? "one"
                    : mod10 is >= 2 and <= 4 && mod100 is not (>= 12 and <= 14) ? "few"
                    : "many";
            case "cs" or "sk":
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other";
            case "fr" or "pt":
                return n is 0 or 1 ? "one" : "other";
            case "ja" or "zh" or "ko" or "vi" or "th" or "id":
                return "other";
            default:
                return n == 1 ? "one" : "other";
        }
    }
}

/// <summary>An error before it is turned into text – the text depends on the language of the request.</summary>
internal readonly record struct ErrorMessage(string Key, string Code, object?[] Args, long? Count = null)
{
    public static ErrorMessage Of(string key, string code, params object?[] args) => new(key, code, args);

    public static ErrorMessage Counted(string key, string code, long count, params object?[] args) => new(key, code, args, count);
}
