using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DynamicEndpoints.Analyzers;

/// <summary>
/// Reports processors and validators of the compilation that end up with the same registration name
/// (<c>[DynamicProcessor("name")]</c> / <c>[DynamicValidator("name")]</c> or derived from the type name) – registering
/// both, e.g. with <c>AddFromAssembly</c>, fails at start-up.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RegistrationNameAnalyzer : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Diagnostics.DuplicateProcessorName, Diagnostics.DuplicateValidatorName];

    private sealed class Entry(INamedTypeSymbol type, string name, Location location)
    {
        public INamedTypeSymbol Type { get; } = type;
        public string Name { get; } = name;
        public Location Location { get; } = location;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var processor = start.Compilation.GetTypeByMetadataName(KnownTypes.Processor);
            var validator = start.Compilation.GetTypeByMetadataName(KnownTypes.Validator);
            if (processor is null && validator is null)
            {
                return;
            }

            var processors = new ConcurrentBag<Entry>();
            var validators = new ConcurrentBag<Entry>();
            start.RegisterSymbolAction(c => Collect((INamedTypeSymbol)c.Symbol, processors, validators), SymbolKind.NamedType);
            start.RegisterCompilationEndAction(end =>
            {
                ReportDuplicates(end, processors, Diagnostics.DuplicateProcessorName);
                ReportDuplicates(end, validators, Diagnostics.DuplicateValidatorName);
            });
        });
    }

    private static void Collect(INamedTypeSymbol type, ConcurrentBag<Entry> processors, ConcurrentBag<Entry> validators)
    {
        // Same filter as assembly scanning: concrete, non-generic classes.
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType || type.IsImplicitlyDeclared || type.Locations.Length == 0)
        {
            return;
        }

        if (KnownTypes.Implements(type, KnownTypes.Processor))
        {
            Add(processors, type, KnownTypes.ProcessorAttribute, "Processor");
        }

        // FluentValidation validators are only counted when they carry [DynamicValidator] – plain ones are often
        // application validators that never get registered as dynamic validators.
        if (KnownTypes.Implements(type, KnownTypes.Validator) ||
            (KnownTypes.Implements(type, KnownTypes.FluentValidator) && HasAttribute(type, KnownTypes.ValidatorAttribute)))
        {
            Add(validators, type, KnownTypes.ValidatorAttribute, "Validator");
        }
    }

    private static void Add(ConcurrentBag<Entry> entries, INamedTypeSymbol type, string attribute, string suffix)
    {
        var name = KnownTypes.RegistrationName(type, attribute, suffix, out var location);
        if (!string.IsNullOrEmpty(name))
        {
            entries.Add(new Entry(type, name!, location ?? type.Locations[0]));
        }
    }

    private static bool HasAttribute(INamedTypeSymbol type, string attribute) =>
        type.GetAttributes().Any(a => KnownTypes.Is(a.AttributeClass, attribute));

    private static void ReportDuplicates(CompilationAnalysisContext context, IEnumerable<Entry> entries, DiagnosticDescriptor descriptor)
    {
        var groups = entries
            .GroupBy(e => e.Name, System.StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var group in groups)
        {
            var members = group.OrderBy(e => e.Type.ToDisplayString(), System.StringComparer.Ordinal).ToList();
            foreach (var entry in members)
            {
                var others = string.Join(", ", members.Where(m => !ReferenceEquals(m, entry)).Select(m => $"'{m.Type.ToDisplayString()}'"));
                context.ReportDiagnostic(Diagnostic.Create(descriptor, entry.Location, entry.Name, entry.Type.ToDisplayString(), others));
            }
        }
    }
}
