using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>What an <c>ef-crud</c> endpoint does with its entity.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CrudOperation>))]
public enum CrudOperation
{
    /// <summary><c>GET</c>: a page of entities, filtered and sorted – <c>{ "items": [...], "page": 1, "pageSize": 50, "total": 120 }</c>.</summary>
    List,
    /// <summary><c>GET</c> with the key in the route: one entity, with its <c>ETag</c>; <c>404</c> when it doesn't exist.</summary>
    Get,
    /// <summary><c>POST</c>: creates an entity from the body – <c>201</c> with <c>Location</c> and <c>ETag</c>.</summary>
    Create,
    /// <summary><c>PUT</c>: replaces the writable fields the endpoint declares; absent ones are cleared.</summary>
    Update,
    /// <summary><c>PATCH</c>: changes only the fields sent.</summary>
    Patch,
    /// <summary><c>DELETE</c>: removes the entity – <c>204</c>.</summary>
    Delete,
}

/// <summary>The operations an entity allows (<see cref="DynamicCrudEntityBuilder{TEntity}.Operations"/>).</summary>
[Flags]
public enum CrudOperations
{
    None = 0,
    List = 1,
    Get = 2,
    Create = 4,
    Update = 8,
    Patch = 16,
    Delete = 32,
    /// <summary><see cref="List"/> and <see cref="Get"/>.</summary>
    Read = List | Get,
    /// <summary><see cref="Create"/>, <see cref="Update"/>, <see cref="Patch"/> and <see cref="Delete"/>.</summary>
    Write = Create | Update | Patch | Delete,
    All = Read | Write,
}

/// <summary>Comparison of a list filter. Strings: eq, ne, contains, startsWith, in; numbers, dates and times: eq, ne, lt, lte, gt, gte, in; booleans: eq, ne; GUIDs and enums: eq, ne, in.</summary>
[JsonConverter(typeof(CamelCaseEnumConverter<CrudFilterOperator>))]
public enum CrudFilterOperator
{
    Eq,
    Ne,
    Lt,
    Lte,
    Gt,
    Gte,
    /// <summary>Substring match of a string field. Case sensitivity follows the database collation.</summary>
    Contains,
    /// <summary>Prefix match of a string field. Case sensitivity follows the database collation.</summary>
    StartsWith,
    /// <summary>The field equals one of the values of an array (at most 100).</summary>
    In,
}

/// <summary>Configuration of the <c>ef-crud</c> processor. The entity is one the application exposed with <c>AddEntityFrameworkCrud</c>.</summary>
public sealed class DynamicCrudConfig
{
    /// <summary>Name of an exposed entity, e.g. <c>products</c> – never a CLR type name.</summary>
    public string? Entity { get; set; }

    [JsonConverter(typeof(CamelCaseEnumConverter<CrudOperation>))]
    public CrudOperation Operation { get; set; } = CrudOperation.List;

    /// <summary>
    /// Route parameter with the key (get, update, patch, delete). Default: the name of the key field, e.g. <c>id</c>. Only entities with a
    /// single-property key support these operations.
    /// </summary>
    public string? Key { get; set; }

    /// <summary>List: page size when the request has no <c>pageSize</c> parameter.</summary>
    [Range(1, 10_000)]
    public int PageSize { get; set; } = 50;

    /// <summary>List: the largest <c>pageSize</c> a request may ask for.</summary>
    [Range(1, 10_000)]
    public int MaxPageSize { get; set; } = 200;

    /// <summary>List: include <c>total</c>, the number of matching entities (one more <c>COUNT</c> query). Default <c>true</c>.</summary>
    public bool IncludeTotal { get; set; } = true;

    /// <summary>List: default order, sortable fields separated by commas, <c>-</c> for descending – <c>name,-price</c>.</summary>
    public string? Sort { get; set; }

    /// <summary>List: query parameter with the order the client wants (same syntax as <see cref="Sort"/>), e.g. <c>sort</c>.</summary>
    public string? SortParameter { get; set; }

    /// <summary>List: filters on filterable fields; all of them must match.</summary>
    public List<DynamicCrudFilter>? Filters { get; set; }

    /// <summary>Update, patch, delete: answer <c>428</c> when the request has no <c>If-Match</c> header (entities with a concurrency token).</summary>
    public bool RequireIfMatch { get; set; }
}

/// <summary>A filter of a list endpoint: <see cref="Field"/> compared with a request parameter or a fixed value.</summary>
public sealed class DynamicCrudFilter
{
    /// <summary>A filterable field of the entity.</summary>
    public string? Field { get; set; }

    [JsonConverter(typeof(CamelCaseEnumConverter<CrudFilterOperator>))]
    public CrudFilterOperator Operator { get; set; } = CrudFilterOperator.Eq;

    /// <summary>Query, header or route parameter with the value. The filter is skipped when the request doesn't have it.</summary>
    public string? Parameter { get; set; }

    /// <summary>A fixed value instead of <see cref="Parameter"/> – an array for <see cref="CrudFilterOperator.In"/>.</summary>
    public JsonNode? Value { get; set; }
}

/// <summary>
/// Name of an entity in <c>ef-crud</c> definitions when it is exposed without an explicit one (the attribute wins over the
/// <c>DbSet</c> property name). The name, not the CLR type, is what definitions store.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class DynamicEntityAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

// Enum values as camelCase strings ("startsWith"); reading is case-insensitive.
internal sealed class CamelCaseEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;
