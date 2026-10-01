using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Validation.Engine;

/// <summary>
/// Evaluator of JsonLogic rules (https://jsonlogic.com) following the reference implementation's JavaScript semantics:
/// truthiness, loose equality, and string-vs-number comparisons (two strings compare lexically, so ISO dates work).
/// Pure and side-effect free – rules can only read the data they are given.
/// </summary>
internal static class JsonLogic
{
    public static readonly IReadOnlyList<string> Operators =
    [
        "var", "missing", "missing_some",
        "if", "?:", "and", "or", "!", "!!",
        "==", "===", "!=", "!==", "<", "<=", ">", ">=",
        "+", "-", "*", "/", "%", "min", "max",
        "in", "cat", "substr", "merge",
        "all", "some", "none", "map", "filter", "reduce",
    ];

    private static readonly HashSet<string> Known = [.. Operators];

    /// <summary>Structural problems: unknown operators or objects that are not single-operator expressions.</summary>
    public static List<string> Validate(JsonNode? rule)
    {
        var problems = new List<string>();
        Validate(rule, "#", problems);
        return problems;
    }

    public static JsonNode? Apply(JsonNode? rule, JsonNode? data)
    {
        switch (rule)
        {
            case JsonArray array:
                return new JsonArray(array.Select(r => Apply(r, data)).ToArray());
            case JsonObject { Count: 1 } expression:
                var (op, raw) = expression.First();
                var args = raw is JsonArray list ? list.ToList() : [raw];
                return Operate(op, args, data);
            default:
                return rule?.DeepClone();
        }
    }

    public static bool IsTruthy(JsonNode? value) => JsonValues.KindOf(value) switch
    {
        JsonValueKind.Null => false,
        JsonValueKind.False => false,
        JsonValueKind.True => true,
        JsonValueKind.Number => JsonValues.TryGetNumber(value, out var d) && d != 0,
        JsonValueKind.String => value!.GetValue<string>().Length > 0,
        JsonValueKind.Array => value!.AsArray().Count > 0,
        _ => true,
    };

    private static JsonNode? Operate(string op, List<JsonNode?> a, JsonNode? data)
    {
        JsonNode? Arg(int i) => i < a.Count ? Apply(a[i], data) : null;

        switch (op)
        {
            case "var":
                var value = Resolve(data, Arg(0));
                return value ?? (a.Count > 1 ? Arg(1) : null);

            case "missing":
            {
                var keys = a.Count > 0 && Arg(0) is JsonArray first ? first.ToList() : a.Select(x => Apply(x, data)).ToList();
                return new JsonArray(Missing(data, keys).ToArray());
            }

            case "missing_some":
            {
                var need = ToNumber(Arg(0)) ?? 0;
                var keys = (Arg(1) as JsonArray)?.ToList() ?? [];
                var missing = Missing(data, keys).ToList();
                return keys.Count - missing.Count >= need ? new JsonArray() : new JsonArray(missing.ToArray());
            }

            case "if":
            case "?:":
                for (var i = 0; i + 1 < a.Count; i += 2)
                {
                    if (IsTruthy(Apply(a[i], data)))
                    {
                        return Apply(a[i + 1], data);
                    }
                }

                return a.Count % 2 == 1 ? Apply(a[^1], data) : null;

            case "and":
            {
                JsonNode? last = null;
                foreach (var item in a)
                {
                    last = Apply(item, data);
                    if (!IsTruthy(last))
                    {
                        return last;
                    }
                }

                return last;
            }

            case "or":
            {
                JsonNode? last = null;
                foreach (var item in a)
                {
                    last = Apply(item, data);
                    if (IsTruthy(last))
                    {
                        return last;
                    }
                }

                return last;
            }

            case "!": return !IsTruthy(Arg(0));
            case "!!": return IsTruthy(Arg(0));
            case "==": return LooseEquals(Arg(0), Arg(1));
            case "!=": return !LooseEquals(Arg(0), Arg(1));
            case "===": return StrictEquals(Arg(0), Arg(1));
            case "!==": return !StrictEquals(Arg(0), Arg(1));

            case "<":
                return a.Count == 3
                    ? LessThan(Arg(0), Arg(1)) && LessThan(Arg(1), Arg(2))
                    : LessThan(Arg(0), Arg(1));
            case "<=":
                return a.Count == 3
                    ? LessOrEqual(Arg(0), Arg(1)) && LessOrEqual(Arg(1), Arg(2))
                    : LessOrEqual(Arg(0), Arg(1));
            case ">": return LessThan(Arg(1), Arg(0));
            case ">=": return LessOrEqual(Arg(1), Arg(0));

            case "+": return Arithmetic(a.Select(x => ToNumber(Apply(x, data))), 0m, (x, y) => x + y);
            case "*": return Arithmetic(a.Select(x => ToNumber(Apply(x, data))), 1m, (x, y) => x * y);
            case "-":
                return a.Count == 1
                    ? Number(-ToNumber(Arg(0)))
                    : Number(Calc(ToNumber(Arg(0)), ToNumber(Arg(1)), (x, y) => x - y));
            case "/": return Number(Calc(ToNumber(Arg(0)), ToNumber(Arg(1)), (x, y) => y == 0 ? null : x / y));
            case "%": return Number(Calc(ToNumber(Arg(0)), ToNumber(Arg(1)), (x, y) => y == 0 ? null : x % y));

            case "min":
            case "max":
            {
                var numbers = a.Select(x => ToNumber(Apply(x, data))).ToList();
                if (numbers.Count == 0 || numbers.Any(n => n is null))
                {
                    return null;
                }

                return Number(op == "min" ? numbers.Min() : numbers.Max());
            }

            case "in":
            {
                var needle = Arg(0);
                return Arg(1) switch
                {
                    JsonArray haystack => haystack.Any(item => StrictEquals(item, needle)),
                    var s when JsonValues.TryGetString(s, out var text) => text.Contains(ToJsString(needle), StringComparison.Ordinal),
                    _ => false,
                };
            }

            case "cat":
                return string.Concat(a.Select(x => ToJsString(Apply(x, data))));

            case "substr":
            {
                var text = ToJsString(Arg(0));
                var start = (int)(ToNumber(Arg(1)) ?? 0);
                if (start < 0)
                {
                    start = Math.Max(0, text.Length + start);
                }

                start = Math.Min(start, text.Length);
                if (a.Count < 3)
                {
                    return text[start..];
                }

                var length = (int)(ToNumber(Arg(2)) ?? 0);
                var end = length < 0 ? Math.Max(start, text.Length + length) : Math.Min(text.Length, start + length);
                return text[start..end];
            }

            case "merge":
            {
                var merged = new JsonArray();
                foreach (var item in a.Select(x => Apply(x, data)))
                {
                    if (item is JsonArray array)
                    {
                        foreach (var element in array.ToList())
                        {
                            array.Remove(element);
                            merged.Add(element);
                        }
                    }
                    else
                    {
                        merged.Add(item);
                    }
                }

                return merged;
            }

            case "all":
            case "some":
            case "none":
            {
                var items = Arg(0) as JsonArray;
                if (items is null || items.Count == 0)
                {
                    return op == "none";
                }

                var rule = a.Count > 1 ? a[1] : null;
                var results = items.Select(item => IsTruthy(Apply(rule, item)));
                return op switch
                {
                    "all" => results.All(r => r),
                    "some" => results.Any(r => r),
                    _ => !results.Any(r => r),
                };
            }

            case "map":
            case "filter":
            {
                var items = Arg(0) as JsonArray ?? [];
                var rule = a.Count > 1 ? a[1] : null;
                var result = new JsonArray();
                foreach (var item in items)
                {
                    if (op == "map")
                    {
                        result.Add(Apply(rule, item));
                    }
                    else if (IsTruthy(Apply(rule, item)))
                    {
                        result.Add(item?.DeepClone());
                    }
                }

                return result;
            }

            case "reduce":
            {
                var items = Arg(0) as JsonArray ?? [];
                var rule = a.Count > 1 ? a[1] : null;
                var accumulator = Arg(2);
                foreach (var item in items)
                {
                    accumulator = Apply(rule, new JsonObject { ["current"] = item?.DeepClone(), ["accumulator"] = accumulator });
                }

                return accumulator;
            }

            default:
                throw new InvalidOperationException($"Unknown JsonLogic operator '{op}'.");
        }
    }

    private static void Validate(JsonNode? rule, string at, List<string> problems)
    {
        switch (rule)
        {
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    Validate(array[i], $"{at}/{i}", problems);
                }

                break;
            case JsonObject obj when obj.Count != 1:
                problems.Add($"{at}: an expression must have exactly one operator, e.g. {{\"==\": [1, 1]}}.");
                break;
            case JsonObject obj:
                var (op, args) = obj.First();
                if (!Known.Contains(op))
                {
                    problems.Add($"{at}: unknown operator '{op}'. Supported: {string.Join(" ", Operators)}.");
                }

                Validate(args, $"{at}/{op}", problems);
                break;
        }
    }

    // --------------------------------------------------------------- data access

    private static JsonNode? Resolve(JsonNode? data, JsonNode? path)
    {
        var key = path is null ? string.Empty : ToJsString(path);
        if (key.Length == 0)
        {
            return data?.DeepClone();
        }

        var current = data;
        foreach (var segment in key.Split('.'))
        {
            current = current switch
            {
                JsonObject obj => obj[segment],
                JsonArray array when int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < array.Count => array[i],
                _ => null,
            };
            if (current is null)
            {
                return null;
            }
        }

        return current?.DeepClone();
    }

    private static IEnumerable<JsonNode?> Missing(JsonNode? data, IEnumerable<JsonNode?> keys) =>
        keys.Where(k => Resolve(data, k) is var v && (v is null || (JsonValues.TryGetString(v, out var s) && s.Length == 0)))
            .Select(k => k?.DeepClone());

    // --------------------------------------------------------------- JavaScript semantics

    private static bool LooseEquals(JsonNode? x, JsonNode? y)
    {
        var kx = JsonValues.KindOf(x);
        var ky = JsonValues.KindOf(y);
        if (kx == JsonValueKind.Null || ky == JsonValueKind.Null)
        {
            return kx == ky;
        }

        if (IsBool(kx) || IsBool(ky) || kx == JsonValueKind.Number || ky == JsonValueKind.Number)
        {
            if (kx is JsonValueKind.Object || ky is JsonValueKind.Object)
            {
                return false;
            }

            var nx = ToNumber(x);
            var ny = ToNumber(y);
            return nx is not null && ny is not null && nx == ny;
        }

        // string/array combinations compare as strings (JS ToPrimitive); objects only by reference
        if (kx is JsonValueKind.Object || ky is JsonValueKind.Object)
        {
            return false;
        }

        return string.Equals(ToJsString(x), ToJsString(y), StringComparison.Ordinal) &&
            (kx == JsonValueKind.String || ky == JsonValueKind.String);
    }

    private static bool StrictEquals(JsonNode? x, JsonNode? y)
    {
        var kind = JsonValues.KindOf(x);
        if (IsBool(kind) && IsBool(JsonValues.KindOf(y)))
        {
            return kind == JsonValues.KindOf(y);
        }

        return kind == JsonValues.KindOf(y) && kind is not (JsonValueKind.Array or JsonValueKind.Object) && JsonValues.DeepEquals(x, y);
    }

    private static bool LessThan(JsonNode? x, JsonNode? y) => Compare(x, y) is < 0;

    private static bool LessOrEqual(JsonNode? x, JsonNode? y) => Compare(x, y) is <= 0;

    /// <returns><c>null</c> when the values are not comparable (NaN in JavaScript).</returns>
    private static int? Compare(JsonNode? x, JsonNode? y)
    {
        if (JsonValues.TryGetString(x, out var sx) && JsonValues.TryGetString(y, out var sy))
        {
            return Math.Sign(string.CompareOrdinal(sx, sy));
        }

        var nx = ToNumber(x);
        var ny = ToNumber(y);
        return nx is null || ny is null ? null : nx.Value.CompareTo(ny.Value);
    }

    private static decimal? ToNumber(JsonNode? value)
    {
        switch (JsonValues.KindOf(value))
        {
            case JsonValueKind.Null: return 0;
            case JsonValueKind.True: return 1;
            case JsonValueKind.False: return 0;
            case JsonValueKind.Number: return JsonValues.TryGetNumber(value, out var d) ? d : null;
            case JsonValueKind.String:
                var text = value!.GetValue<string>().Trim();
                if (text.Length == 0)
                {
                    return 0;
                }

                return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            case JsonValueKind.Array:
                var array = value!.AsArray();
                return array.Count switch { 0 => 0, 1 => ToNumber(array[0]), _ => null };
            default:
                return null;
        }
    }

    private static string ToJsString(JsonNode? value) => JsonValues.KindOf(value) switch
    {
        JsonValueKind.Null => "null",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => JsonValues.TryGetNumber(value, out var d) ? JsonValues.FormatNumber(d) : "NaN",
        JsonValueKind.String => value!.GetValue<string>(),
        JsonValueKind.Array => string.Join(",", value!.AsArray().Select(v => v is null ? string.Empty : ToJsString(v))),
        _ => "[object Object]",
    };

    private static JsonNode? Arithmetic(IEnumerable<decimal?> values, decimal seed, Func<decimal, decimal, decimal> op)
    {
        var result = seed;
        foreach (var value in values)
        {
            if (value is null)
            {
                return null;
            }

            try
            {
                result = op(result, value.Value);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return Number(result);
    }

    private static decimal? Calc(decimal? x, decimal? y, Func<decimal, decimal, decimal?> op)
    {
        if (x is null || y is null)
        {
            return null;
        }

        try
        {
            return op(x.Value, y.Value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static JsonNode? Number(decimal? value) => value is null ? null : JsonValue.Create(value.Value);

    private static bool IsBool(JsonValueKind kind) => kind is JsonValueKind.True or JsonValueKind.False;
}
