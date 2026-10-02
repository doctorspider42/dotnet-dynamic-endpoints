using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace DynamicEndpoints.Analyzers;

/// <summary>
/// Checks calls of the fluent API (<c>DynamicEndpoint</c>, <c>ParameterBuilder</c>) and of the registration extensions:
/// type arguments, typed configurations and the literal routes, patterns and rules that would otherwise only fail when an
/// endpoint is saved.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FluentApiAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
    [
        Diagnostics.ProcessorTypeNotConcrete,
        Diagnostics.ValidatorTypeInvalid,
        Diagnostics.ConfigurationTypeMismatch,
        Diagnostics.InvalidRouteTemplate,
        Diagnostics.InvalidRegex,
        Diagnostics.UnsupportedRegexConstruct,
        Diagnostics.InvalidRuleJson,
        Diagnostics.InvalidJsonLogic,
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var types = new Types(start.Compilation);
            if (types.DynamicEndpoint is null)
            {
                return; // DynamicEndpoints is not referenced
            }

            start.RegisterOperationAction(c => AnalyzeInvocation(c, types), OperationKind.Invocation);
        });
    }

    private sealed class Types(Compilation compilation)
    {
        public readonly INamedTypeSymbol? DynamicEndpoint = compilation.GetTypeByMetadataName(KnownTypes.DynamicEndpoint);
        public readonly INamedTypeSymbol? ParameterBuilder = compilation.GetTypeByMetadataName(KnownTypes.ParameterBuilder);
        public readonly INamedTypeSymbol? ServiceCollectionExtensions = compilation.GetTypeByMetadataName(KnownTypes.ServiceCollectionExtensions);
        public readonly INamedTypeSymbol? FluentValidationExtensions = compilation.GetTypeByMetadataName(KnownTypes.FluentValidationExtensions);
        public readonly INamedTypeSymbol? JsonNode = compilation.GetTypeByMetadataName("System.Text.Json.Nodes.JsonNode");
        public readonly INamedTypeSymbol? JsonDocument = compilation.GetTypeByMetadataName("System.Text.Json.JsonDocument");
        public readonly INamedTypeSymbol? Enumerable = compilation.GetTypeByMetadataName("System.Collections.IEnumerable");
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, Types types)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var method = invocation.TargetMethod;
        var owner = method.ContainingType;

        if (Same(owner, types.DynamicEndpoint))
        {
            switch (method.Name)
            {
                case "Get" or "Post" or "Put" or "Patch" or "Delete" when method.IsStatic:
                    CheckRoute(context, invocation);
                    break;
                case "HandledBy" when method.IsGenericMethod:
                    CheckProcessorType(context, invocation, method.TypeArguments[0]);
                    CheckConfiguration(context, invocation, method, KnownTypes.TypedProcessor, types);
                    break;
                case "ValidatedBy" when method.IsGenericMethod:
                    CheckValidatorType(context, invocation, method.TypeArguments[0], requireValidator: true);
                    CheckConfiguration(context, invocation, method, KnownTypes.TypedValidator, types);
                    break;
                case "WithRule":
                    CheckRule(context, invocation);
                    break;
            }
        }
        else if (Same(owner, types.ParameterBuilder))
        {
            switch (method.Name)
            {
                case "ValidatedBy" when method.IsGenericMethod:
                    CheckValidatorType(context, invocation, method.TypeArguments[0], requireValidator: true);
                    CheckConfiguration(context, invocation, method, KnownTypes.TypedValidator, types);
                    break;
                case "Pattern":
                    CheckPattern(context, invocation);
                    break;
            }
        }
        else if (Same(owner, types.ServiceCollectionExtensions) || Same(owner, types.FluentValidationExtensions))
        {
            // The constraints already require the right interface, only abstract types slip through.
            switch (method.Name)
            {
                case "AddProcessor" when method.IsGenericMethod:
                    CheckProcessorType(context, invocation, method.TypeArguments[0]);
                    break;
                case "AddValidator" or "AddFluentValidator" when method.IsGenericMethod:
                    CheckValidatorType(context, invocation, method.TypeArguments[0], requireValidator: false);
                    break;
            }
        }
    }

    private static bool Same(ISymbol? a, ISymbol? b) => b is not null && SymbolEqualityComparer.Default.Equals(a?.OriginalDefinition, b);

    // ------------------------------------------------------------------ DE0001, DE0002

    private static void CheckProcessorType(OperationAnalysisContext context, IInvocationOperation invocation, ITypeSymbol type)
    {
        var reason = type switch
        {
            ITypeParameterSymbol or IErrorTypeSymbol => null, // unknown until run time / already a compiler error
            { TypeKind: TypeKind.Interface } => "it is an interface",
            { TypeKind: TypeKind.Struct } => "it is a struct, processors are registered as classes",
            { IsAbstract: true } => "it is abstract",
            _ => null,
        };
        if (reason is not null)
        {
            Report(context, Diagnostics.ProcessorTypeNotConcrete, TypeArgumentLocation(invocation), Display(type), reason);
        }
    }

    private static void CheckValidatorType(OperationAnalysisContext context, IInvocationOperation invocation, ITypeSymbol type, bool requireValidator)
    {
        if (type is ITypeParameterSymbol or IErrorTypeSymbol)
        {
            return;
        }

        string? reason = null;
        if (requireValidator && !KnownTypes.Implements(type, KnownTypes.Validator) && !KnownTypes.Implements(type, KnownTypes.FluentValidator))
        {
            reason = "it implements neither IDynamicValidator nor FluentValidation's IValidator";
        }
        else if (type.TypeKind == TypeKind.Interface)
        {
            reason = "it is an interface, use a concrete validator class";
        }
        else if (type.IsAbstract)
        {
            reason = "it is abstract, use a concrete validator class";
        }

        if (reason is not null)
        {
            Report(context, Diagnostics.ValidatorTypeInvalid, TypeArgumentLocation(invocation), Display(type), reason);
        }
    }

    // ------------------------------------------------------------------ DE0005

    private static void CheckConfiguration(OperationAnalysisContext context, IInvocationOperation invocation, IMethodSymbol method, string typedBase, Types types)
    {
        // Only the HandledBy<T>(object?) / ValidatedBy<T>(object?) overloads – the two-type-argument ones are constrained.
        if (method.TypeArguments.Length != 1 || invocation.Arguments.Length == 0)
        {
            return;
        }

        var argument = invocation.Arguments[0];
        if (argument.ArgumentKind != ArgumentKind.Explicit || argument.Parameter?.Type.SpecialType != SpecialType.System_Object)
        {
            return;
        }

        var value = argument.Value;
        while (value is IConversionOperation { IsImplicit: true } conversion)
        {
            value = conversion.Operand;
        }

        var passed = value.Type;
        var expected = KnownTypes.FindConfigurationType(method.TypeArguments[0], typedBase);
        if (expected is null || passed is not { TypeKind: TypeKind.Class } || passed.IsAnonymousType ||
            passed.SpecialType is SpecialType.System_Object or SpecialType.System_String ||
            InheritsFrom(passed, types.JsonNode) || InheritsFrom(passed, types.JsonDocument) ||
            (types.Enumerable is not null && passed.AllInterfaces.Contains(types.Enumerable, SymbolEqualityComparer.Default)) ||
            InheritsFrom(passed, expected as INamedTypeSymbol))
        {
            return;
        }

        Report(context, Diagnostics.ConfigurationTypeMismatch, argument.Syntax.GetLocation(),
            Display(method.TypeArguments[0]), Display(expected), Display(passed));
    }

    private static bool InheritsFrom(ITypeSymbol type, INamedTypeSymbol? baseType)
    {
        if (baseType is null)
        {
            return false;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ DE0006 – DE0010

    private static void CheckRoute(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (ConstantString(invocation, 0) is { } route && RouteTemplateChecker.Check(route) is { } problem)
        {
            Report(context, Diagnostics.InvalidRouteTemplate, invocation.Arguments[0].Syntax.GetLocation(), route, problem);
        }
    }

    private static void CheckPattern(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        if (ConstantString(invocation, 0) is not { } pattern)
        {
            return;
        }

        var location = invocation.Arguments[0].Syntax.GetLocation();
        if (RegexChecker.FindSyntaxError(pattern) is { } error)
        {
            Report(context, Diagnostics.InvalidRegex, location, pattern, error);
        }
        else if (RegexChecker.FindUnsupportedConstruct(pattern) is { } construct)
        {
            Report(context, Diagnostics.UnsupportedRegexConstruct, location, pattern, construct);
        }
    }

    private static void CheckRule(OperationAnalysisContext context, IInvocationOperation invocation)
    {
        // WithRule(string condition, …) only – the JsonNode overload is checked by the compiler of the definition.
        if (invocation.Arguments.Length == 0 || invocation.Arguments[0].Parameter?.Type.SpecialType != SpecialType.System_String ||
            ConstantString(invocation, 0) is not { } rule)
        {
            return;
        }

        if (JsonLogicChecker.Check(rule, out var isSyntaxError) is { } problem)
        {
            Report(context, isSyntaxError ? Diagnostics.InvalidRuleJson : Diagnostics.InvalidJsonLogic,
                invocation.Arguments[0].Syntax.GetLocation(), problem);
        }
    }

    /// <summary>The compile-time constant value of an argument (literals, constants, constant interpolations).</summary>
    private static string? ConstantString(IInvocationOperation invocation, int ordinal)
    {
        foreach (var argument in invocation.Arguments)
        {
            if (argument.Parameter?.Ordinal == ordinal && argument.ArgumentKind == ArgumentKind.Explicit)
            {
                return argument.Value.ConstantValue is { HasValue: true, Value: string text } ? text : null;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ helpers

    private static Location TypeArgumentLocation(IInvocationOperation invocation)
    {
        // Point at the generic name (HandledBy<Foo>) rather than at the whole fluent chain.
        var syntax = invocation.Syntax;
        if (syntax is Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax call)
        {
            syntax = call.Expression switch
            {
                Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax member => member.Name,
                var expression => expression,
            };
        }

        return syntax.GetLocation();
    }

    private static string Display(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat);

    private static void Report(OperationAnalysisContext context, DiagnosticDescriptor descriptor, Location location, params object[] args) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, args));
}
