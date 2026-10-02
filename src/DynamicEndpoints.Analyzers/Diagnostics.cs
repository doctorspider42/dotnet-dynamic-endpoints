using Microsoft.CodeAnalysis;

namespace DynamicEndpoints.Analyzers;

/// <summary>All diagnostics reported by the DynamicEndpoints analyzers.</summary>
internal static class Diagnostics
{
    private const string Category = "DynamicEndpoints";

    public static readonly DiagnosticDescriptor ProcessorTypeNotConcrete = Warning(
        "DE0001",
        "Processor type cannot be instantiated",
        "'{0}' cannot be used as a processor: {1}. Use a concrete class that implements IDynamicEndpointProcessor.",
        "Processors are resolved from DI by their concrete type, so HandledBy<T>() and AddProcessor<T>() need a non-abstract class.");

    public static readonly DiagnosticDescriptor ValidatorTypeInvalid = Warning(
        "DE0002",
        "Type is not a usable validator",
        "'{0}' cannot be used as a validator: {1}",
        "ValidatedBy<T>() only accepts concrete IDynamicValidator implementations (or DynamicValidator<TConfiguration>) and FluentValidation validators.");

    public static readonly DiagnosticDescriptor DuplicateProcessorName = Warning(
        "DE0003",
        "Duplicate processor name",
        "Processor name '{0}' of '{1}' is also used by {2}. Registering both fails at start-up; set a unique name with [DynamicProcessor(\"…\")].",
        "Processor names (from [DynamicProcessor] or derived from the type name) are compared case-insensitively and must be unique.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor DuplicateValidatorName = Warning(
        "DE0004",
        "Duplicate validator name",
        "Validator name '{0}' of '{1}' is also used by {2}. Registering both fails at start-up; set a unique name with [DynamicValidator(\"…\")].",
        "Validator names (from [DynamicValidator] or derived from the type name) are compared case-insensitively and must be unique.",
        WellKnownDiagnosticTags.CompilationEnd);

    public static readonly DiagnosticDescriptor ConfigurationTypeMismatch = Warning(
        "DE0005",
        "Configuration type does not match",
        "'{0}' expects a configuration of type '{1}', but '{2}' is passed. Pass a '{1}' or use the strongly typed overload.",
        "Typed processors and validators deserialize their configuration strictly into TConfiguration; another class usually fails validation when the endpoint is saved.");

    public static readonly DiagnosticDescriptor InvalidRouteTemplate = Warning(
        "DE0006",
        "Invalid route template",
        "Invalid route template '{0}': {1}",
        "The route is parsed by ASP.NET Core routing when the endpoint is saved; an invalid template is rejected.");

    public static readonly DiagnosticDescriptor InvalidRegex = Warning(
        "DE0007",
        "Invalid regular expression",
        "Invalid regular expression '{0}': {1}",
        "Parameter patterns are compiled when the endpoint is saved; an invalid pattern is rejected.");

    public static readonly DiagnosticDescriptor UnsupportedRegexConstruct = Warning(
        "DE0008",
        "Regular expression construct is not supported",
        "Regular expression '{0}' uses {1}, which RegexOptions.NonBacktracking does not support",
        "Parameter patterns run on the non-backtracking engine (no catastrophic backtracking), which does not support backreferences, lookarounds, atomic groups, conditionals, balancing groups or \\G.");

    public static readonly DiagnosticDescriptor InvalidRuleJson = Warning(
        "DE0009",
        "Rule is not valid JSON",
        "The rule is not valid JSON: {0}",
        "WithRule(string, …) parses the condition as JSON right away; invalid JSON throws.");

    public static readonly DiagnosticDescriptor InvalidJsonLogic = Warning(
        "DE0010",
        "Invalid JsonLogic rule",
        "Invalid JsonLogic rule: {0}",
        "Rules are checked when the endpoint is saved: every object must be a single-operator expression with a supported operator.");

    private static DiagnosticDescriptor Warning(string id, string title, string message, string description, params string[] tags) =>
        new(id, title, message, Category, DiagnosticSeverity.Warning, isEnabledByDefault: true, description, customTags: tags);
}
