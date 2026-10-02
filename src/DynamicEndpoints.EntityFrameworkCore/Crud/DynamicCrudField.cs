using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>An exposed property of an entity.</summary>
internal sealed class DynamicCrudField
{
    public DynamicCrudField(IProperty property, bool readOnly, bool filterable, bool sortable)
    {
        Property = property;
        Name = DynamicCrudNames.Field(property.Name);
        ValueType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        Kind = DynamicCrudValues.KindOf(ValueType) ?? throw new InvalidOperationException($"Type {property.ClrType.Name} is not supported.");

        // Store-generated values, concurrency tokens and shadow properties are never written; non-generated keys only on create.
        var generated = property.ValueGenerated != ValueGenerated.Never;
        Creatable = !readOnly && !generated && !property.IsConcurrencyToken && !property.IsShadowProperty();
        Writable = Creatable && !property.IsKey();
        Filterable = filterable;
        Sortable = sortable;
    }

    public IProperty Property { get; }

    /// <summary>JSON name, camelCase.</summary>
    public string Name { get; }

    public string PropertyName => Property.Name;

    /// <summary>The CLR type without <see cref="Nullable{T}"/>.</summary>
    public Type ValueType { get; }

    public DynamicCrudValueKind Kind { get; }

    public bool IsNullable => Property.IsNullable;

    /// <summary>Can be set by create.</summary>
    public bool Creatable { get; }

    /// <summary>Can be set by update and patch.</summary>
    public bool Writable { get; }

    public bool ReadOnly => !Creatable;

    public bool Filterable { get; }

    public bool Sortable { get; }

    public bool IsKey => Property.IsPrimaryKey();

    /// <summary>Required on create and update: non-nullable strings without a generated value (value types default to their CLR default).</summary>
    public bool Required => !IsNullable && Kind == DynamicCrudValueKind.String && Property.ValueGenerated == ValueGenerated.Never;

    public IReadOnlyList<CrudFilterOperator> Operators => Kind switch
    {
        DynamicCrudValueKind.String => [CrudFilterOperator.Eq, CrudFilterOperator.Ne, CrudFilterOperator.Contains, CrudFilterOperator.StartsWith, CrudFilterOperator.In],
        DynamicCrudValueKind.Boolean => [CrudFilterOperator.Eq, CrudFilterOperator.Ne],
        DynamicCrudValueKind.Guid or DynamicCrudValueKind.Enum => [CrudFilterOperator.Eq, CrudFilterOperator.Ne, CrudFilterOperator.In],
        _ => [CrudFilterOperator.Eq, CrudFilterOperator.Ne, CrudFilterOperator.Lt, CrudFilterOperator.Lte, CrudFilterOperator.Gt, CrudFilterOperator.Gte, CrudFilterOperator.In],
    };

    public IReadOnlyList<string>? EnumValues => Kind == DynamicCrudValueKind.Enum ? Enum.GetNames(ValueType) : null;

    public int? MaxLength => Kind == DynamicCrudValueKind.String ? Property.GetMaxLength() : null;

    public JsonNode? ToJson(object? value) => DynamicCrudValues.ToJson(value, Kind);

    public bool TryFromJson(JsonNode? node, out object? value) => DynamicCrudValues.TryFromJson(node, this, out value);

    /// <summary>JSON Schema of the field in responses (and request bodies).</summary>
    public JsonObject Schema(bool forResponse)
    {
        var schema = new JsonObject();
        string type;
        switch (Kind)
        {
            case DynamicCrudValueKind.Integer:
                type = "integer";
                if (DynamicCrudValues.IntegerRange(ValueType) is var (min, max))
                {
                    schema["minimum"] = min;
                    schema["maximum"] = max;
                }

                break;
            case DynamicCrudValueKind.Number:
                type = "number";
                if (DynamicCrudValues.DecimalLimit(Property) is { } limit)
                {
                    schema["minimum"] = -limit;
                    schema["maximum"] = limit;
                }

                break;
            case DynamicCrudValueKind.Boolean:
                type = "boolean";
                break;
            case DynamicCrudValueKind.Guid:
                type = "string";
                schema["format"] = "uuid";
                break;
            case DynamicCrudValueKind.DateTime:
                type = "string";
                schema["format"] = "date-time";
                break;
            case DynamicCrudValueKind.Date:
                type = "string";
                schema["format"] = "date";
                break;
            case DynamicCrudValueKind.Time:
                type = "string";
                schema["format"] = "time";
                break;
            case DynamicCrudValueKind.Enum:
                type = "string";
                schema["enum"] = new JsonArray(Enum.GetNames(ValueType).Select(n => (JsonNode?)n).ToArray());
                break;
            default:
                type = "string";
                if (MaxLength is { } maxLength)
                {
                    schema["maxLength"] = maxLength;
                }

                break;
        }

        schema["type"] = IsNullable ? new JsonArray(type, "null") : type;
        if (forResponse && ReadOnly)
        {
            schema["readOnly"] = true;
        }

        if (schema["enum"] is JsonArray values && IsNullable)
        {
            values.Add(null);
        }

        // "type" first reads better.
        return new JsonObject(schema.OrderBy(p => p.Key == "type" ? 0 : 1).Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone())));
    }
}

internal enum DynamicCrudValueKind
{
    String,
    Integer,
    Number,
    Boolean,
    Guid,
    DateTime,
    Date,
    Time,
    Enum,
}

/// <summary>Conversions between field values and JSON.</summary>
internal static class DynamicCrudValues
{
    public static DynamicCrudValueKind? KindOf(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum)
        {
            return DynamicCrudValueKind.Enum;
        }

        return Type.GetTypeCode(type) switch
        {
            TypeCode.String => DynamicCrudValueKind.String,
            TypeCode.Boolean => DynamicCrudValueKind.Boolean,
            TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64
                => DynamicCrudValueKind.Integer,
            TypeCode.Single or TypeCode.Double or TypeCode.Decimal => DynamicCrudValueKind.Number,
            TypeCode.DateTime => DynamicCrudValueKind.DateTime,
            _ when type == typeof(Guid) => DynamicCrudValueKind.Guid,
            _ when type == typeof(DateTimeOffset) => DynamicCrudValueKind.DateTime,
            _ when type == typeof(DateOnly) => DynamicCrudValueKind.Date,
            _ when type == typeof(TimeOnly) => DynamicCrudValueKind.Time,
            _ => null,
        };
    }

    /// <summary>Bounds of the small integer types; <c>null</c> for <c>int</c> and wider (the JSON number range is enough of a hint).</summary>
    public static (long Min, long Max)? IntegerRange(Type type) => Type.GetTypeCode(type) switch
    {
        TypeCode.Byte => (byte.MinValue, byte.MaxValue),
        TypeCode.SByte => (sbyte.MinValue, sbyte.MaxValue),
        TypeCode.Int16 => (short.MinValue, short.MaxValue),
        TypeCode.UInt16 => (ushort.MinValue, ushort.MaxValue),
        TypeCode.UInt32 => (uint.MinValue, uint.MaxValue),
        _ => null,
    };

    /// <summary>The largest value of a decimal column with precision and scale: (18, 2) → 9999999999999999.99.</summary>
    public static decimal? DecimalLimit(IProperty property)
    {
        if ((Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) != typeof(decimal) ||
            property.GetPrecision() is not (> 0 and <= 28 and var precision))
        {
            return null;
        }

        var scale = Math.Clamp(property.GetScale() ?? 0, 0, precision);
        var digits = precision - scale;
        var step = 1m;
        for (var i = 0; i < scale; i++)
        {
            step /= 10;
        }

        var max = 1m;
        for (var i = 0; i < digits; i++)
        {
            max *= 10;
        }

        return max - step;
    }

    public static JsonNode? ToJson(object? value, DynamicCrudValueKind kind) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        Guid g => JsonValue.Create(g.ToString()),
        DateTime d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
        DateTimeOffset d => JsonValue.Create(d.ToString("O", CultureInfo.InvariantCulture)),
        DateOnly d => JsonValue.Create(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        TimeOnly t => JsonValue.Create(t.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture)),
        Enum e => JsonValue.Create(e.ToString()),
        _ when kind is DynamicCrudValueKind.Integer or DynamicCrudValueKind.Number => JsonSerializer.SerializeToNode(value, value.GetType()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>A request value as a value of the field's CLR type. <c>null</c> only for nullable fields.</summary>
    public static bool TryFromJson(JsonNode? node, DynamicCrudField field, out object? value)
    {
        value = null;
        if (node is null)
        {
            return field.IsNullable;
        }

        if (node is not JsonValue json)
        {
            return false;
        }

        var type = field.ValueType;
        try
        {
            switch (field.Kind)
            {
                case DynamicCrudValueKind.String when json.TryGetValue<string>(out var s):
                    value = s;
                    return true;
                case DynamicCrudValueKind.Boolean when json.TryGetValue<bool>(out var b):
                    value = b;
                    return true;
                case DynamicCrudValueKind.Integer or DynamicCrudValueKind.Number when json.GetValueKind() == JsonValueKind.Number:
                    value = json.Deserialize(type);
                    return value is not null && (type != typeof(double) && type != typeof(float) || double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                case DynamicCrudValueKind.Guid when json.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g):
                    value = g;
                    return true;
                case DynamicCrudValueKind.DateTime when json.TryGetValue<string>(out var s) && type == typeof(DateTimeOffset):
                    value = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    return true;
                case DynamicCrudValueKind.DateTime when json.TryGetValue<string>(out var s):
                    value = DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    return true;
                case DynamicCrudValueKind.Date when json.TryGetValue<string>(out var s) &&
                    DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    value = date;
                    return true;
                case DynamicCrudValueKind.Time when json.TryGetValue<string>(out var s) &&
                    TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time):
                    value = time;
                    return true;
                case DynamicCrudValueKind.Enum when json.TryGetValue<string>(out var s) &&
                    Enum.GetNames(type).FirstOrDefault(n => string.Equals(n, s, StringComparison.OrdinalIgnoreCase)) is { } name:
                    value = Enum.Parse(type, name);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or OverflowException or InvalidOperationException)
        {
            value = null;
            return false;
        }
    }
}
