using System.ComponentModel;

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

public sealed class DynamicEndpointConcurrencyException(Guid id, int expectedRevision, int? actualRevision)
    : DynamicEndpointException(actualRevision is null
        ? $"Dynamic endpoint '{id}' was modified concurrently (expected revision {expectedRevision})."
        : $"Dynamic endpoint '{id}' was modified concurrently (expected revision {expectedRevision}, current revision {actualRevision}).")
{
    public Guid Id { get; } = id;
    public int ExpectedRevision { get; } = expectedRevision;
    public int? ActualRevision { get; } = actualRevision;

    [Obsolete("Renamed to ExpectedRevision.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int ExpectedVersion => ExpectedRevision;

    [Obsolete("Renamed to ActualRevision.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public int? ActualVersion => ActualRevision;
}
