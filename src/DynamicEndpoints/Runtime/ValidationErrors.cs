namespace DynamicEndpoints.Runtime;

internal sealed class ValidationErrors(DynamicValidationMessages? messages = null)
{
    // Definition checks (admin facing) use the default English texts.
    private static readonly DynamicValidationMessages DefaultMessages = new();

    private readonly DynamicValidationMessages _messages = messages ?? DefaultMessages;
    private readonly Dictionary<string, List<DynamicValidationError>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public bool Contains(string key) => _errors.ContainsKey(key);

    /// <summary>Errors for <paramref name="key"/> itself or anything nested in it (<c>key.x</c>, <c>key[0]</c>).</summary>
    public bool HasErrorsFor(string key) => _errors.Keys.Any(k =>
        k.StartsWith(key, StringComparison.Ordinal) && (k.Length == key.Length || k[key.Length] is '.' or '['));

    public void Add(string key, ErrorMessage message) => Add(key, _messages.Format(message), message.Code);

    public void Add(string key, string message, string code = DynamicValidationCodes.Custom)
    {
        if (!_errors.TryGetValue(key, out var errors))
        {
            _errors[key] = errors = [];
        }

        if (!errors.Any(e => e.Message == message))
        {
            errors.Add(new DynamicValidationError(key, code, message));
        }
    }

    public string Format(ErrorMessage message) => _messages.Format(message);

    public IReadOnlyList<DynamicValidationError> ToList() => _errors.Values.SelectMany(e => e).ToList();

    public Dictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(e => e.Key, e => e.Value.Select(m => m.Message).ToArray(), StringComparer.Ordinal);

    public IEnumerable<string> Flatten() =>
        _errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m.Message}"));
}
