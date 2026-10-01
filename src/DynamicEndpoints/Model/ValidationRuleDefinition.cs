using System.Text.Json.Nodes;

namespace DynamicEndpoints;

/// <summary>
/// A business rule expressed in JsonLogic (https://jsonlogic.com). The rule passes when <see cref="Condition"/>
/// evaluates to a truthy value against the normalized parameter object, e.g.
/// <c>{"&lt;": [{"var": "startDate"}, {"var": "endDate"}]}</c>.
/// </summary>
public sealed record ValidationRuleDefinition
{
    public string? Name { get; init; }

    public JsonNode? Condition { get; init; }

    /// <summary>Error message returned when the rule fails.</summary>
    public string Message { get; init; } = "";

    /// <summary>Parameter the error is reported for. When empty, the error is reported under <c>request</c>.</summary>
    public string? Parameter { get; init; }
}
