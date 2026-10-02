using static DynamicEndpoints.Analyzers.Tests.AnalyzerHarness;

namespace DynamicEndpoints.Analyzers.Tests;

/// <summary>DE0003, DE0004 – duplicate registration names.</summary>
public sealed class RegistrationNameTests
{
    private const string Processor = "public Task<IResult> ProcessAsync(DynamicRequest request) => Task.FromResult(Results.Ok());";
    private const string Validator = "public ValueTask ValidateAsync(DynamicValidationContext context) => ValueTask.CompletedTask;";

    [Fact]
    public async Task Unique_names_produce_no_diagnostics()
    {
        await NoDiagnosticsAsync($$"""
            namespace A
            {
                public sealed class OrderLookupProcessor : IDynamicEndpointProcessor { {{Processor}} }
                [DynamicProcessor("orders")] public sealed class OrderProcessor : IDynamicEndpointProcessor { {{Processor}} }
                public sealed class NipValidator : IDynamicValidator { {{Validator}} }
                // Same derived name as the processor above, but processors and validators are separate registries.
                public sealed class OrderLookupValidator : IDynamicValidator { {{Validator}} }
                // Abstract and generic types are never registered by scanning.
                public abstract class Base : IDynamicEndpointProcessor { {{Processor}} }
                public sealed class Generic<T> : IDynamicEndpointProcessor { {{Processor}} }
            }

            namespace B
            {
                // Plain FluentValidation validators are application validators until they are marked with [DynamicValidator].
                public sealed class NipValidator : FluentValidation.AbstractValidator<string> { }
                public sealed class Abstract : A.Base { }
            }

            namespace FluentValidation
            {
                public interface IValidator { }
                public abstract class AbstractValidator<T> : IValidator { }
            }
            """);
    }

    [Fact]
    public async Task Duplicate_processor_names_are_reported_on_each_type()
    {
        var findings = await AnalyzeAsync($$"""
            namespace A
            {
                public sealed class OrderLookupProcessor : IDynamicEndpointProcessor { {{Processor}} }
            }

            namespace B
            {
                public sealed class OrderLookupProcessor : IDynamicEndpointProcessor { {{Processor}} }
                [DynamicProcessor("Order-Lookup")] public sealed class Lookup : IDynamicEndpointProcessor { {{Processor}} }
                [DynamicProcessor("echo")] public sealed class Echo1 : IDynamicEndpointProcessor { {{Processor}} }
                public sealed class EchoProcessor : IDynamicEndpointProcessor { {{Processor}} }
            }
            """);

        Assert.Equal(
            [
                ("DE0003", "OrderLookupProcessor"),
                ("DE0003", "OrderLookupProcessor"),
                ("DE0003", "DynamicProcessor(\"Order-Lookup\")"),
                ("DE0003", "DynamicProcessor(\"echo\")"),
                ("DE0003", "EchoProcessor"),
            ],
            findings.Select(f => (f.Id, f.Text)));
        Assert.Contains("'order-lookup' of 'A.OrderLookupProcessor' is also used by 'B.Lookup', 'B.OrderLookupProcessor'", findings[0].Message);
    }

    [Fact]
    public async Task Duplicate_validator_names_are_reported_on_each_type()
    {
        var findings = await AnalyzeAsync($$"""
            public sealed class NipValidator : IDynamicValidator { {{Validator}} }
            [DynamicValidator("NIP")] public sealed class TaxIdValidator : IDynamicValidator { {{Validator}} }
            [DynamicValidator("nip")] public sealed class FluentNip : FluentValidation.AbstractValidator<string> { }

            namespace FluentValidation
            {
                public interface IValidator { }
                public abstract class AbstractValidator<T> : IValidator { }
            }
            """);

        Assert.Equal(["NipValidator", "DynamicValidator(\"NIP\")", "DynamicValidator(\"nip\")"], findings.Select(f => f.Text));
        Assert.All(findings, f => Assert.Equal("DE0004", f.Id));
    }
}
