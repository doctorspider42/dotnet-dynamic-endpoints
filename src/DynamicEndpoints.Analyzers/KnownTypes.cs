using Microsoft.CodeAnalysis;

namespace DynamicEndpoints.Analyzers;

/// <summary>Metadata names of the library types the analyzers look for – the analyzer never references DynamicEndpoints itself.</summary>
internal static class KnownTypes
{
    public const string DynamicEndpoint = "DynamicEndpoints.DynamicEndpoint";
    public const string ParameterBuilder = "DynamicEndpoints.ParameterBuilder";
    public const string ServiceCollectionExtensions = "Microsoft.Extensions.DependencyInjection.DynamicEndpointsServiceCollectionExtensions";
    public const string FluentValidationExtensions = "Microsoft.Extensions.DependencyInjection.DynamicEndpointsFluentValidationExtensions";

    public const string Processor = "DynamicEndpoints.IDynamicEndpointProcessor";
    public const string TypedProcessor = "DynamicEndpoints.DynamicEndpointProcessor`1";
    public const string ProcessorAttribute = "DynamicEndpoints.DynamicProcessorAttribute";
    public const string Validator = "DynamicEndpoints.IDynamicValidator";
    public const string TypedValidator = "DynamicEndpoints.DynamicValidator`1";
    public const string ValidatorAttribute = "DynamicEndpoints.DynamicValidatorAttribute";
    public const string FluentValidator = "FluentValidation.IValidator";

    public static string MetadataName(ITypeSymbol type) =>
        type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() + "." + type.MetadataName : type.MetadataName;

    public static bool Is(ITypeSymbol? type, string metadataName) => type is not null && MetadataName(type) == metadataName;

    /// <summary>Whether <paramref name="type"/> is or implements the interface.</summary>
    public static bool Implements(ITypeSymbol type, string interfaceMetadataName)
    {
        if (Is(type, interfaceMetadataName))
        {
            return true;
        }

        foreach (var candidate in type.AllInterfaces)
        {
            if (Is(candidate, interfaceMetadataName))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The <c>TConfiguration</c> of the first base class that is <paramref name="genericBaseMetadataName"/>.</summary>
    public static ITypeSymbol? FindConfigurationType(ITypeSymbol type, string genericBaseMetadataName)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && Is(current.OriginalDefinition, genericBaseMetadataName))
            {
                return current.TypeArguments[0];
            }
        }

        return null;
    }

    /// <summary>The name a type is registered under: <c>[DynamicProcessor("name")]</c>, or derived as in <c>RegistryNames.Derive</c>.</summary>
    public static string? RegistrationName(INamedTypeSymbol type, string attributeMetadataName, string suffix, out Location? location)
    {
        location = null;
        foreach (var attribute in type.GetAttributes())
        {
            if (Is(attribute.AttributeClass, attributeMetadataName))
            {
                location = attribute.ApplicationSyntaxReference?.GetSyntax().GetLocation();
                return attribute.ConstructorArguments.Length == 1 ? attribute.ConstructorArguments[0].Value as string : null;
            }
        }

        return Derive(type.Name, suffix);
    }

    // ("OrderLookupProcessor", "Processor") -> "order-lookup"
    public static string Derive(string name, string suffix)
    {
        if (name.EndsWith(suffix, System.StringComparison.Ordinal) && name.Length > suffix.Length)
        {
            name = name.Substring(0, name.Length - suffix.Length);
        }

        var chars = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
            {
                chars.Append('-');
            }

            chars.Append(char.ToLowerInvariant(name[i]));
        }

        return chars.ToString();
    }
}
