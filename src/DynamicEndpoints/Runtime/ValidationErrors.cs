namespace DynamicEndpoints.Runtime;

internal sealed class ValidationErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool HasErrors => _errors.Count > 0;

    public bool Contains(string key) => _errors.ContainsKey(key);

    /// <summary>Errors for <paramref name="key"/> itself or anything nested in it (<c>key.x</c>, <c>key[0]</c>).</summary>
    public bool HasErrorsFor(string key) => _errors.Keys.Any(k =>
        k.StartsWith(key, StringComparison.Ordinal) && (k.Length == key.Length || k[key.Length] is '.' or '['));

    public void Add(string key, string message)
    {
        if (!_errors.TryGetValue(key, out var messages))
        {
            _errors[key] = messages = [];
        }

        if (!messages.Contains(message))
        {
            messages.Add(message);
        }
    }

    public Dictionary<string, string[]> ToDictionary() =>
        _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal);

    public IEnumerable<string> Flatten() =>
        _errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"));
}
