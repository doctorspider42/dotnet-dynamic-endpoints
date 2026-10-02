using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DynamicEndpoints.Analyzers.Tests;

/// <summary>Compiles a snippet against the real DynamicEndpoints.dll and the shared frameworks, then runs the analyzers.</summary>
internal static class AnalyzerHarness
{
    private const string Usings = """
        using System;
        using System.Collections.Generic;
        using System.Text.Json.Nodes;
        using System.Threading.Tasks;
        using DynamicEndpoints;
        using Microsoft.AspNetCore.Http;
        using Microsoft.Extensions.DependencyInjection;

        """;

    // Everything the test host runs on: the .NET and ASP.NET Core shared frameworks plus DynamicEndpoints.dll.
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(typeof(DynamicEndpoint).Assembly.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());

    /// <summary>A reported diagnostic: its id, the flagged source text and the message.</summary>
    public sealed record Finding(string Id, string Text, string Message);

    /// <summary>Diagnostics of the analyzers in source order.</summary>
    public static async Task<List<Finding>> AnalyzeAsync(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(Usings + source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            "AnalyzerTests",
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        // The snippets must be valid C#, otherwise a test could pass for the wrong reason.
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, "The test source does not compile:\n" + string.Join("\n", errors));

        ImmutableArray<DiagnosticAnalyzer> analyzers = [new FluentApiAnalyzer(), new RegistrationNameAnalyzer()];
        var diagnostics = await compilation.WithAnalyzers(analyzers).GetAnalyzerDiagnosticsAsync();
        return diagnostics
            .OrderBy(d => d.Location.SourceSpan.Start)
            .Select(d => new Finding(d.Id, tree.GetText().ToString(d.Location.SourceSpan), d.GetMessage(CultureInfo.InvariantCulture)))
            .ToList();
    }

    /// <summary>Asserts that the snippet produces no diagnostics at all.</summary>
    public static async Task NoDiagnosticsAsync(string source) => Assert.Empty(await AnalyzeAsync(source));

    /// <summary>Asserts a single diagnostic with the given id on <paramref name="flagged"/> whose message contains <paramref name="message"/>.</summary>
    public static async Task SingleAsync(string source, string id, string flagged, string message = "")
    {
        var findings = await AnalyzeAsync(source);
        var finding = Assert.Single(findings);
        Assert.Equal(id, finding.Id);
        Assert.Equal(flagged, finding.Text);
        Assert.Contains(message, finding.Message);
    }

    /// <summary>Wraps statements (and type declarations) into a method that has a services builder <c>b</c> at hand.</summary>
    public static string InMethod(string statements, string types = "") => $$"""
        {{types}}

        public static class Snippet
        {
            public static void Run(IDynamicEndpointsBuilder b)
            {
                {{statements}}
            }
        }
        """;
}
