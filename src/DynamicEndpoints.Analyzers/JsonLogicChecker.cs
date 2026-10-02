using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DynamicEndpoints.Analyzers;

/// <summary>
/// Validates JsonLogic rules: strict JSON (as <c>JsonNode.Parse</c> reads it – no comments, no trailing commas, no
/// duplicate keys) and the structural checks of the runtime's <c>JsonLogic.Validate</c>.
/// </summary>
internal static class JsonLogicChecker
{
    /// <summary>Must stay in sync with <c>JsonLogic.Operators</c> in src/DynamicEndpoints/Validation/Engine/JsonLogic.cs.</summary>
    public static readonly string[] Operators =
    [
        "var", "missing", "missing_some",
        "if", "?:", "and", "or", "!", "!!",
        "==", "===", "!=", "!==", "<", "<=", ">", ">=",
        "+", "-", "*", "/", "%", "min", "max",
        "in", "cat", "substr", "merge",
        "all", "some", "none", "map", "filter", "reduce",
    ];

    private static readonly HashSet<string> Known = [.. Operators];

    // Parsed JSON: an object is a list of key/value pairs, an array a list of values, anything else a scalar.
    private sealed class Node
    {
        public List<KeyValuePair<string, Node>>? Properties;
        public List<Node>? Items;
        public bool IsNull;
    }

    /// <summary>Returns the JSON syntax error, or <c>null</c> (then <paramref name="root"/> is set).</summary>
    private static string? Parse(string json, out Node? root)
    {
        var parser = new Parser(json);
        try
        {
            parser.SkipWhitespace();
            root = parser.ParseValue(depth: 0);
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                throw parser.Error("unexpected content after the JSON value");
            }

            return null;
        }
        catch (JsonSyntaxException ex)
        {
            root = null;
            return ex.Message;
        }
    }

    /// <summary>Checks a rule; <paramref name="isSyntaxError"/> tells invalid JSON apart from invalid JsonLogic.</summary>
    public static string? Check(string json, out bool isSyntaxError)
    {
        var error = Parse(json, out var root);
        isSyntaxError = error is not null;
        if (error is not null)
        {
            return error;
        }

        if (root!.IsNull)
        {
            return "a rule needs a condition, not null";
        }

        return Validate(root, "#");
    }

    // Mirrors JsonLogic.Validate: arrays are walked, every object must be a single, known operator.
    private static string? Validate(Node node, string at)
    {
        if (node.Items is { } items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                if (Validate(items[i], at + "/" + i.ToString(CultureInfo.InvariantCulture)) is { } problem)
                {
                    return problem;
                }
            }
        }
        else if (node.Properties is { } properties)
        {
            if (properties.Count != 1)
            {
                return $"{at}: an expression must have exactly one operator, e.g. {{\"==\": [1, 1]}}";
            }

            var op = properties[0].Key;
            if (!Known.Contains(op))
            {
                return $"{at}: unknown operator '{op}'. Supported: {string.Join(" ", Operators)}";
            }

            return Validate(properties[0].Value, at + "/" + op);
        }

        return null;
    }

    private sealed class JsonSyntaxException(string message) : System.Exception(message);

    private sealed class Parser(string text)
    {
        private const int MaxDepth = 64; // JsonNode.Parse default
        private int _position;

        public bool AtEnd => _position >= text.Length;

        public JsonSyntaxException Error(string message) =>
            new($"{message} (at position {_position.ToString(CultureInfo.InvariantCulture)})");

        public void SkipWhitespace()
        {
            while (!AtEnd && text[_position] is ' ' or '\t' or '\n' or '\r')
            {
                _position++;
            }
        }

        public Node ParseValue(int depth)
        {
            if (AtEnd)
            {
                throw Error("unexpected end of the JSON");
            }

            switch (text[_position])
            {
                case '{': return ParseObject(depth + 1);
                case '[': return ParseArray(depth + 1);
                case '"':
                    ParseString();
                    return new Node();
                case 't':
                    Expect("true");
                    return new Node();
                case 'f':
                    Expect("false");
                    return new Node();
                case 'n':
                    Expect("null");
                    return new Node { IsNull = true };
                case var c when c == '-' || c is >= '0' and <= '9':
                    ParseNumber();
                    return new Node();
                default:
                    throw Error($"'{text[_position]}' is not a valid start of a JSON value");
            }
        }

        private Node ParseObject(int depth)
        {
            CheckDepth(depth);
            _position++; // {
            var node = new Node { Properties = [] };
            var keys = new HashSet<string>(System.StringComparer.Ordinal);
            SkipWhitespace();
            if (Peek() == '}')
            {
                _position++;
                return node;
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"')
                {
                    throw Error("expected a property name in double quotes");
                }

                var key = ParseString();
                if (!keys.Add(key))
                {
                    throw Error($"the property '{key}' is defined more than once");
                }

                SkipWhitespace();
                if (Peek() != ':')
                {
                    throw Error("expected ':' after a property name");
                }

                _position++;
                SkipWhitespace();
                node.Properties.Add(new KeyValuePair<string, Node>(key, ParseValue(depth)));
                SkipWhitespace();
                switch (Peek())
                {
                    case ',':
                        _position++;
                        continue;
                    case '}':
                        _position++;
                        return node;
                    default:
                        throw Error("expected ',' or '}' in an object (trailing commas are not allowed)");
                }
            }
        }

        private Node ParseArray(int depth)
        {
            CheckDepth(depth);
            _position++; // [
            var node = new Node { Items = [] };
            SkipWhitespace();
            if (Peek() == ']')
            {
                _position++;
                return node;
            }

            while (true)
            {
                SkipWhitespace();
                node.Items.Add(ParseValue(depth));
                SkipWhitespace();
                switch (Peek())
                {
                    case ',':
                        _position++;
                        continue;
                    case ']':
                        _position++;
                        return node;
                    default:
                        throw Error("expected ',' or ']' in an array (trailing commas are not allowed)");
                }
            }
        }

        private string ParseString()
        {
            _position++; // opening quote
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                {
                    throw Error("unterminated string");
                }

                var c = text[_position++];
                if (c == '"')
                {
                    return value.ToString();
                }

                if (c < ' ')
                {
                    throw Error("control characters must be escaped in strings");
                }

                if (c != '\\')
                {
                    value.Append(c);
                    continue;
                }

                if (AtEnd)
                {
                    throw Error("unterminated string");
                }

                var escape = text[_position++];
                switch (escape)
                {
                    case '"': value.Append('"'); break;
                    case '\\': value.Append('\\'); break;
                    case '/': value.Append('/'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'u':
                        if (_position + 4 > text.Length ||
                            !int.TryParse(text.Substring(_position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        {
                            throw Error("'\\u' must be followed by four hex digits");
                        }

                        value.Append((char)code);
                        _position += 4;
                        break;
                    default:
                        throw Error($"'\\{escape}' is not a valid escape sequence");
                }
            }
        }

        private void ParseNumber()
        {
            if (Peek() == '-')
            {
                _position++;
            }

            if (Peek() == '0')
            {
                _position++;
                if (IsDigit(Peek()))
                {
                    throw Error("numbers cannot have leading zeros");
                }
            }
            else
            {
                Digits();
            }

            if (Peek() == '.')
            {
                _position++;
                Digits();
            }

            if (Peek() is 'e' or 'E')
            {
                _position++;
                if (Peek() is '+' or '-')
                {
                    _position++;
                }

                Digits();
            }
        }

        private void Digits()
        {
            if (!IsDigit(Peek()))
            {
                throw Error("expected a digit");
            }

            while (IsDigit(Peek()))
            {
                _position++;
            }
        }

        private void Expect(string literal)
        {
            if (string.CompareOrdinal(text, _position, literal, 0, literal.Length) != 0)
            {
                throw Error($"invalid literal, expected '{literal}'");
            }

            _position += literal.Length;
        }

        private void CheckDepth(int depth)
        {
            if (depth > MaxDepth)
            {
                throw Error($"the JSON is nested deeper than {MaxDepth.ToString(CultureInfo.InvariantCulture)} levels");
            }
        }

        private char Peek() => AtEnd ? '\0' : text[_position];

        private static bool IsDigit(char c) => c is >= '0' and <= '9';
    }
}
