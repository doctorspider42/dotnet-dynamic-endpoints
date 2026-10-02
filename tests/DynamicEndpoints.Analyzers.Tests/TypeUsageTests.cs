using static DynamicEndpoints.Analyzers.Tests.AnalyzerHarness;

namespace DynamicEndpoints.Analyzers.Tests;

/// <summary>DE0001, DE0002, DE0005 – type arguments and typed configurations.</summary>
public sealed class TypeUsageTests
{
    internal const string Types = """
        public sealed class Config { public string? Name { get; set; } }
        public class BaseConfig { public string? Name { get; set; } }
        public sealed class ExtendedConfig : BaseConfig { }
        public sealed class OtherConfig { public int Count { get; set; } }

        public sealed class EchoProcessor : IDynamicEndpointProcessor
        {
            public Task<IResult> ProcessAsync(DynamicRequest request) => Task.FromResult(Results.Ok());
        }

        public abstract class BaseProcessor : IDynamicEndpointProcessor
        {
            public abstract Task<IResult> ProcessAsync(DynamicRequest request);
        }

        public interface ICustomProcessor : IDynamicEndpointProcessor { }

        public struct StructProcessor : IDynamicEndpointProcessor
        {
            public Task<IResult> ProcessAsync(DynamicRequest request) => Task.FromResult(Results.Ok());
        }

        public sealed class TypedProcessor : DynamicEndpointProcessor<Config>
        {
            protected override Task<IResult> ProcessAsync(DynamicRequest request, Config configuration) => Task.FromResult(Results.Ok());
        }

        public sealed class BaseConfigProcessor : DynamicEndpointProcessor<BaseConfig>
        {
            protected override Task<IResult> ProcessAsync(DynamicRequest request, BaseConfig configuration) => Task.FromResult(Results.Ok());
        }

        public abstract class TypedProcessorBase : DynamicEndpointProcessor<Config> { }

        public sealed class PlainValidator : IDynamicValidator
        {
            public ValueTask ValidateAsync(DynamicValidationContext context) => ValueTask.CompletedTask;
        }

        public abstract class BaseValidator : IDynamicValidator
        {
            public abstract ValueTask ValidateAsync(DynamicValidationContext context);
        }

        public sealed class TypedValidator : DynamicValidator<Config>
        {
            protected override ValueTask ValidateAsync(DynamicValidationContext context, Config configuration) => ValueTask.CompletedTask;
        }

        public sealed class NotAValidator { }

        public sealed class BookingValidator : FluentValidation.AbstractValidator<Config> { }

        namespace FluentValidation
        {
            public interface IValidator { }
            public abstract class AbstractValidator<T> : IValidator { }
        }
        """;

    [Fact]
    public async Task Valid_processor_and_validator_usages_produce_no_diagnostics()
    {
        await NoDiagnosticsAsync(InMethod("""
            b.AddProcessor<EchoProcessor>().AddProcessor<TypedProcessor>().AddValidator<PlainValidator>();
            DynamicEndpoint endpoint = DynamicEndpoint.Get("/orders/{id:int}")
                .HandledBy<EchoProcessor>()
                .HandledBy<EchoProcessor>(new { queue = "incoming" })
                .HandledBy<TypedProcessor>(new Config())
                .HandledBy<TypedProcessor>("{ \"name\": \"x\" }")
                .HandledBy<TypedProcessor>(new JsonObject())
                .HandledBy<TypedProcessor>(new Dictionary<string, object> { ["name"] = "x" })
                .HandledBy<TypedProcessor>((object)new OtherConfig())
                .HandledBy<TypedProcessor>(null)
                .HandledBy<BaseConfigProcessor>(new ExtendedConfig())
                .HandledBy<TypedProcessor, Config>(new Config())
                .ValidatedBy<PlainValidator>()
                .ValidatedBy<TypedValidator>(new Config())
                .ValidatedBy<TypedValidator, Config>(new Config())
                .ValidatedBy<BookingValidator>()
                .FromQuery("id", p => p.ValidatedBy<PlainValidator>().ValidatedBy<BookingValidator>().ValidatedBy<TypedValidator>(new Config()));
            """, Types));
    }

    [Fact]
    public async Task Generic_wrappers_are_not_reported()
    {
        await NoDiagnosticsAsync(InMethod("", Types + """

            public static class Wrappers
            {
                public static DynamicEndpoint Use<T>(DynamicEndpoint e) where T : IDynamicEndpointProcessor => e.HandledBy<T>(new OtherConfig());
                public static DynamicEndpoint Validate<T>(DynamicEndpoint e) where T : class => e.ValidatedBy<T>();
            }
            """));
    }

    [Theory]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<BaseProcessor>();", "HandledBy<BaseProcessor>", "abstract")]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<ICustomProcessor>();", "HandledBy<ICustomProcessor>", "interface")]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<IDynamicEndpointProcessor>();", "HandledBy<IDynamicEndpointProcessor>", "interface")]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<StructProcessor>();", "HandledBy<StructProcessor>", "struct")]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<TypedProcessorBase, Config>(new Config());", "HandledBy<TypedProcessorBase, Config>", "abstract")]
    [InlineData("b.AddProcessor<BaseProcessor>();", "AddProcessor<BaseProcessor>", "abstract")]
    [InlineData("b.AddProcessor<ICustomProcessor>(\"custom\");", "AddProcessor<ICustomProcessor>", "interface")]
    public async Task Processors_that_cannot_be_instantiated_are_reported(string statement, string flagged, string reason)
    {
        await SingleAsync(InMethod(statement, Types), "DE0001", flagged, reason);
    }

    [Theory]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<NotAValidator>();", "ValidatedBy<NotAValidator>", "neither")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<string>();", "ValidatedBy<string>", "neither")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<BaseValidator>();", "ValidatedBy<BaseValidator>", "abstract")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<IDynamicValidator>();", "ValidatedBy<IDynamicValidator>", "interface")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<FluentValidation.AbstractValidator<Config>>();", "ValidatedBy<FluentValidation.AbstractValidator<Config>>", "abstract")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<DynamicValidator<Config>, Config>(new Config());", "ValidatedBy<DynamicValidator<Config>, Config>", "abstract")]
    [InlineData("DynamicEndpoint.Get(\"/a\").FromQuery(\"q\", p => p.ValidatedBy<NotAValidator>());", "ValidatedBy<NotAValidator>", "neither")]
    [InlineData("DynamicEndpoint.Get(\"/a\").FromQuery(\"q\", p => p.ValidatedBy<BaseValidator>());", "ValidatedBy<BaseValidator>", "abstract")]
    [InlineData("b.AddValidator<BaseValidator>();", "AddValidator<BaseValidator>", "abstract")]
    public async Task Types_that_are_not_usable_validators_are_reported(string statement, string flagged, string reason)
    {
        await SingleAsync(InMethod(statement, Types), "DE0002", flagged, reason);
    }

    [Theory]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<TypedProcessor>(new OtherConfig());", "new OtherConfig()")]
    [InlineData("DynamicEndpoint.Get(\"/a\").HandledBy<BaseConfigProcessor>(new OtherConfig { Count = 1 });", "new OtherConfig { Count = 1 }")]
    [InlineData("DynamicEndpoint.Get(\"/a\").ValidatedBy<TypedValidator>(new OtherConfig());", "new OtherConfig()")]
    [InlineData("DynamicEndpoint.Get(\"/a\").FromBody(\"x\", p => p.ValidatedBy<TypedValidator>(new OtherConfig()));", "new OtherConfig()")]
    public async Task Configurations_of_another_class_are_reported(string statement, string flagged)
    {
        await SingleAsync(InMethod(statement, Types), "DE0005", flagged, "expects a configuration of type");
    }

    [Fact]
    public async Task Calls_of_other_types_with_the_same_names_are_ignored()
    {
        await NoDiagnosticsAsync("""
            public sealed class Builder
            {
                public static Builder Get(string route) => new();
                public Builder HandledBy<T>(object? config = null) => this;
                public Builder ValidatedBy<T>() => this;
                public Builder Pattern(string regex) => this;
                public Builder WithRule(string rule) => this;
            }

            public abstract class Abstract { }

            public static class Usage
            {
                public static void Run() => Builder.Get("/a//{").HandledBy<Abstract>(1).ValidatedBy<string>().Pattern("(").WithRule("{");
            }
            """);
    }
}
