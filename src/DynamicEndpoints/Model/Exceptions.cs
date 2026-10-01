namespace DynamicEndpoints;

public class DynamicEndpointException(string message) : Exception(message);

public sealed class DynamicEndpointValidationException(IReadOnlyDictionary<string, string[]> errors)
    : DynamicEndpointException("The dynamic endpoint definition is invalid: " +
        string.Join("; ", errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))))
{
    public DynamicEndpointValidationException(string key, string message)
        : this(new Dictionary<string, string[]> { [key] = [message] })
    {
    }

    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

public sealed class DynamicEndpointNotFoundException(Guid id)
    : DynamicEndpointException($"Dynamic endpoint '{id}' does not exist.")
{
    public Guid Id { get; } = id;
}

public sealed class DynamicEndpointConcurrencyException(Guid id, int expectedVersion, int? actualVersion)
    : DynamicEndpointException(actualVersion is null
        ? $"Dynamic endpoint '{id}' was modified concurrently (expected version {expectedVersion})."
        : $"Dynamic endpoint '{id}' was modified concurrently (expected version {expectedVersion}, current version {actualVersion}).")
{
    public Guid Id { get; } = id;
    public int ExpectedVersion { get; } = expectedVersion;
    public int? ActualVersion { get; } = actualVersion;
}
