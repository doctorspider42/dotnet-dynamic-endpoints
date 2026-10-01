using System.Text.Json.Nodes;

namespace DynamicEndpoints.Sample.Processors;

public enum CalculatorOperation
{
    Sum,
    Product,
    Average,
    Min,
    Max,
}

public sealed class CalculatorConfig
{
    public CalculatorOperation Operation { get; set; } = CalculatorOperation.Sum;

    /// <summary>Parameters to use; all numeric parameters (including numeric arrays) when empty.</summary>
    public List<string> Operands { get; set; } = [];
}

[DynamicProcessor("calculator",
    Description = "Aggregates numeric parameters (sum, product, average, min, max).",
    ConfigurationExample = """{ "operation": "Sum", "operands": ["a", "b"] }""")]
public sealed class CalculatorProcessor : DynamicEndpointProcessor<CalculatorConfig>
{
    protected override Task<IResult> ProcessAsync(DynamicRequest request, CalculatorConfig configuration)
    {
        var names = configuration.Operands.Count > 0 ? configuration.Operands : request.Parameters.Select(p => p.Key).ToList();
        var values = names
            .SelectMany(name => Numbers(request.Parameters[name]))
            .ToList();

        if (values.Count == 0)
        {
            return Task.FromResult(Results.Problem("There is nothing to calculate.", statusCode: StatusCodes.Status422UnprocessableEntity));
        }

        var result = configuration.Operation switch
        {
            CalculatorOperation.Product => values.Aggregate(1m, (a, b) => a * b),
            CalculatorOperation.Average => values.Average(),
            CalculatorOperation.Min => values.Min(),
            CalculatorOperation.Max => values.Max(),
            _ => values.Sum(),
        };

        return Task.FromResult(Results.Ok(new { operation = configuration.Operation.ToString(), values, result }));
    }

    private static IEnumerable<decimal> Numbers(JsonNode? node) => node switch
    {
        JsonArray array => array.SelectMany(Numbers),
        JsonValue value when value.TryGetValue<decimal>(out var d) => [d],
        JsonValue value when value.TryGetValue<long>(out var l) => [l],
        _ => [],
    };
}
