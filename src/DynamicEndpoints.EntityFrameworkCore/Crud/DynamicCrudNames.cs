using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>Entity names of <c>ef-crud</c> definitions.</summary>
internal static class DynamicCrudNames
{
    // Names registered by AddEntityFrameworkCrud, so DynamicEndpoint.HandledByCrud<T>() resolves explicit names too.
    private static readonly ConcurrentDictionary<Type, ConcurrentDictionary<string, byte>> Registered = new();

    /// <summary>[DynamicEntity] → DbSet property name → type name, first letter lower-cased.</summary>
    public static string Default(Type contextType, Type entityType)
    {
        if (entityType.GetCustomAttribute<DynamicEntityAttribute>() is { } attribute)
        {
            return attribute.Name;
        }

        var property = contextType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>) &&
                p.PropertyType.GetGenericArguments()[0] == entityType)
            .Select(p => p.Name)
            .ToList();
        return Camel(property.Count == 1 ? property[0] : TypeName(entityType));
    }

    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= 100 && char.IsAsciiLetter(name[0]) &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static void Register(Type entityType, string name) =>
        Registered.GetOrAdd(entityType, _ => new(StringComparer.OrdinalIgnoreCase)).TryAdd(name, 0);

    /// <summary>The name of an exposed entity type for code – registered name, else <see cref="DynamicEntityAttribute"/>.</summary>
    public static string Of(Type entityType)
    {
        if (Registered.TryGetValue(entityType, out var names))
        {
            var list = names.Keys.ToList();
            if (list.Count == 1)
            {
                return list[0];
            }

            if (list.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{entityType.Name} is exposed under several names ({string.Join(", ", list.Order())}) – pass the entity name to HandledByCrud.");
            }
        }

        return entityType.GetCustomAttribute<DynamicEntityAttribute>()?.Name
            ?? throw new InvalidOperationException(
                $"{entityType.Name} isn't exposed with AddEntityFrameworkCrud(…) (yet) – register it first or pass the entity name to HandledByCrud.");
    }

    /// <summary>JSON name of a property: <c>CreatedAt</c> → <c>createdAt</c>.</summary>
    public static string Field(string property) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(property);

    private static string Camel(string name) => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`');
        return tick > 0 ? name[..tick] : name;
    }
}
