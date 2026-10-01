using System.Text.Json.Nodes;

namespace DynamicEndpoints;

/// <summary>Attaches a registered <see cref="IDynamicValidator"/> to a parameter or to a whole endpoint.</summary>
public sealed record ValidatorReference
{
    public string Name { get; init; } = "";

    /// <summary>Validator specific configuration, checked by the validator when the definition is saved.</summary>
    public JsonObject? Config { get; init; }
}
