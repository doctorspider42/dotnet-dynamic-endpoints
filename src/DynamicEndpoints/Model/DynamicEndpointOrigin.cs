namespace DynamicEndpoints;

/// <summary>
/// Where a definition came from – set by importers, so a later re-import finds the endpoints it created and leaves hand-made ones
/// alone. Stored with the definition (no column of its own); <c>null</c> for endpoints created by hand.
/// </summary>
/// <example><c>{ "kind": "openapi", "document": "Petstore", "operation": "getPet" }</c></example>
public sealed record DynamicEndpointOrigin
{
    /// <summary>Origin of definitions imported from an OpenAPI document.</summary>
    public const string OpenApi = "openapi";

    /// <summary>What created the definition, e.g. <see cref="OpenApi"/>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>The source document, e.g. the OpenAPI document id (its <c>info.title</c> unless given explicitly).</summary>
    public string? Document { get; init; }

    /// <summary>The part of the document, e.g. the <c>operationId</c>, or <c>GET /pets/{id}</c> for operations without one.</summary>
    public string? Operation { get; init; }
}
