using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>An exposed entity resolved against the EF model: its fields, key, tenant column and concurrency tokens.</summary>
internal abstract class DynamicCrudEntity
{
    private readonly List<string> _errors = [];
    private readonly HashSet<string> _allowedTenants;
    private readonly bool _sharedAcrossTenants;

    protected DynamicCrudEntity(DynamicCrudEntityOptions options, IEntityType entityType, DynamicCrudRegistration registration)
    {
        Name = options.Name;
        ClrType = options.ClrType;
        ContextType = registration.ContextType;
        ProcessorName = registration.ProcessorName;
        Operations = options.Operations & CrudOperations.All;
        _allowedTenants = new HashSet<string>(options.AllowedTenants, StringComparer.OrdinalIgnoreCase);
        _sharedAcrossTenants = options.SharedAcrossTenants;

        if (options.TenantProperty is { } tenantName)
        {
            Tenant = entityType.FindProperty(tenantName);
            if (Tenant is null || Tenant.ClrType != typeof(string))
            {
                _errors.Add($"The tenant column '{tenantName}' must be a mapped string property.");
                Tenant = null;
            }
        }

        Fields = ResolveFields(options, entityType);
        ConcurrencyTokens = entityType.GetProperties().Where(p => p.IsConcurrencyToken).ToList();

        if (entityType.FindPrimaryKey() is { Properties: [var key] } && DynamicCrudValues.KindOf(key.ClrType) is not null)
        {
            KeyField = Fields.FirstOrDefault(f => f.Property == key) ?? new DynamicCrudField(key, readOnly: true, filterable: false, sortable: false);
        }
        else if ((Operations & ~CrudOperations.List) != 0)
        {
            _errors.Add("Only entities with a single-property key support get, create, update, patch and delete – use .Operations(CrudOperations.List).");
        }

        if (Operations.HasFlag(CrudOperations.Create) &&
            (ClrType.IsAbstract || ClrType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is null))
        {
            _errors.Add("Create needs a parameterless constructor (it may be private).");
        }

        if (Operations == CrudOperations.None)
        {
            _errors.Add("No operations are allowed.");
        }
    }

    public string Name { get; }

    public Type ClrType { get; }

    public Type ContextType { get; }

    public string ProcessorName { get; }

    public CrudOperations Operations { get; }

    public IReadOnlyList<DynamicCrudField> Fields { get; }

    /// <summary>The single-property primary key; <c>null</c> for keyless entities and composite keys (list only).</summary>
    public DynamicCrudField? KeyField { get; }

    public IProperty? Tenant { get; }

    public IReadOnlyList<IProperty> ConcurrencyTokens { get; }

    /// <summary>Configuration problems; non-empty fails the application start.</summary>
    public IReadOnlyList<string> Errors => _errors;

    public DynamicCrudField? FindField(string? name) =>
        name is null ? null : Fields.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool Allows(CrudOperation operation) => Operations.HasFlag(Flag(operation));

    /// <summary>
    /// Shared endpoints may use every entity. A tenant's endpoints: entities with a tenant column (unless <c>AllowTenants</c> narrows
    /// them), and entities without one only when they are shared across tenants or allowed for the tenant.
    /// </summary>
    public bool IsAvailableTo(string? tenant) =>
        tenant is null ||
        (Tenant is not null ? _allowedTenants.Count == 0 || _allowedTenants.Contains(tenant) : _sharedAcrossTenants || _allowedTenants.Contains(tenant));

    public abstract Task<IResult> ExecuteAsync(DynamicCrudExecution execution);

    /// <summary><c>name,-price</c> → sortable fields with direction (at most 5).</summary>
    public List<(DynamicCrudField Field, bool Descending)> ParseSort(string? sort, out string? error)
    {
        error = null;
        var order = new List<(DynamicCrudField, bool)>();
        var parts = (sort ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 5)
        {
            error = "Sort by at most 5 fields.";
            return [];
        }

        foreach (var part in parts)
        {
            var descending = part.StartsWith('-');
            var name = part.TrimStart('-', '+');
            if (FindField(name) is not { Sortable: true } field)
            {
                var sortable = Fields.Where(f => f.Sortable).Select(f => f.Name).ToList();
                error = $"Can't sort by '{name}'. Sortable: {(sortable.Count == 0 ? "none" : string.Join(", ", sortable))}.";
                return [];
            }

            order.Add((field, descending));
        }

        return order;
    }

    public static CrudOperations Flag(CrudOperation operation) => operation switch
    {
        CrudOperation.List => CrudOperations.List,
        CrudOperation.Get => CrudOperations.Get,
        CrudOperation.Create => CrudOperations.Create,
        CrudOperation.Update => CrudOperations.Update,
        CrudOperation.Patch => CrudOperations.Patch,
        CrudOperation.Delete => CrudOperations.Delete,
        _ => CrudOperations.None,
    };

    public static string MethodOf(CrudOperation operation) => operation switch
    {
        CrudOperation.Create => "POST",
        CrudOperation.Update => "PUT",
        CrudOperation.Patch => "PATCH",
        CrudOperation.Delete => "DELETE",
        _ => "GET",
    };

    private List<DynamicCrudField> ResolveFields(DynamicCrudEntityOptions options, IEntityType entityType)
    {
        var names = new List<string>();
        foreach (var name in options.Except.Where(n => entityType.FindProperty(n) is null && entityType.FindNavigation(n) is null))
        {
            _errors.Add($"'{name}' (except) is not a mapped property.");
        }

        if (options.AllFields)
        {
            // Declaration order, keys first, shadow properties last; types without a JSON mapping (byte[], …) are left out.
            var order = ClrType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select((p, i) => (p.Name, i)).GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First().i);
            names.AddRange(entityType.GetProperties()
                .Where(p => !options.Except.Contains(p.Name) && p != Tenant && DynamicCrudValues.KindOf(p.ClrType) is not null)
                .OrderBy(p => p.IsPrimaryKey() ? 0 : p.IsShadowProperty() ? 2 : 1)
                .ThenBy(p => order.GetValueOrDefault(p.Name, int.MaxValue))
                .Select(p => p.Name));
        }

        foreach (var name in options.Fields.Where(n => !names.Contains(n)))
        {
            if (entityType.FindProperty(name) is not { } property)
            {
                _errors.Add(entityType.FindNavigation(name) is not null || entityType.FindSkipNavigation(name) is not null
                    ? $"'{name}' is a navigation – only scalar properties can be exposed."
                    : $"'{name}' is not a mapped property.");
            }
            else if (DynamicCrudValues.KindOf(property.ClrType) is null)
            {
                _errors.Add($"'{name}' has type {property.ClrType.Name}, which can't be exposed.");
            }
            else
            {
                names.Add(name);
            }
        }

        foreach (var name in options.Filterable.Concat(options.Sortable).Where(n => !names.Contains(n)).Distinct())
        {
            _errors.Add($"'{name}' is filterable or sortable but not an exposed field.");
        }

        return names.Select(n => entityType.FindProperty(n)!)
            .Select(p => new DynamicCrudField(
                p,
                readOnly: options.ReadOnly.Contains(p.Name) || p == Tenant,
                filterable: options.Filterable.Contains(p.Name),
                sortable: options.Sortable.Contains(p.Name)))
            .ToList();
    }
}

/// <summary>One request of an <c>ef-crud</c> endpoint.</summary>
internal sealed record DynamicCrudExecution(DynamicRequest Request, DynamicCrudConfig Config, DbContext Db, string? Tenant);
