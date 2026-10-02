using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// The allowlist of the <c>ef-crud</c> processor: the entities of <typeparamref name="TContext"/> admins may use, and which of their
/// fields. Nothing else of the context is reachable from a definition.
/// </summary>
public sealed class DynamicCrudBuilder<TContext>
    where TContext : DbContext
{
    internal List<DynamicCrudEntityOptions> Entities { get; } = [];

    /// <summary>
    /// Exposes <typeparamref name="TEntity"/> under its default name: <see cref="DynamicEntityAttribute"/>, else the name of its
    /// <c>DbSet</c> property on the context with the first letter lower-cased (<c>Products</c> → <c>products</c>,
    /// <c>OrderLines</c> → <c>orderLines</c>), else the type name the same way (<c>Product</c> → <c>product</c>).
    /// </summary>
    public DynamicCrudBuilder<TContext> Entity<TEntity>(Action<DynamicCrudEntityBuilder<TEntity>> configure)
        where TEntity : class =>
        Entity(DynamicCrudNames.Default(typeof(TContext), typeof(TEntity)), configure);

    /// <summary>Exposes <typeparamref name="TEntity"/> as <paramref name="name"/> (letters, digits, <c>-</c> and <c>_</c>).</summary>
    public DynamicCrudBuilder<TContext> Entity<TEntity>(string name, Action<DynamicCrudEntityBuilder<TEntity>> configure)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (!DynamicCrudNames.IsValid(name))
        {
            throw new ArgumentException($"'{name}' is not a valid entity name – use letters, digits, '-' and '_'.", nameof(name));
        }

        if (Entities.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The entity name '{name}' is used twice.");
        }

        var options = new DynamicCrudEntityOptions(typeof(TEntity), name, static (options, entityType, registration) =>
            new DynamicCrudEntity<TEntity>(options, entityType, registration));
        configure(new DynamicCrudEntityBuilder<TEntity>(options));
        Entities.Add(options);
        return this;
    }
}

/// <summary>What of an entity is exposed. Unlisted fields are never returned and never written.</summary>
public sealed class DynamicCrudEntityBuilder<TEntity>
    where TEntity : class
{
    private readonly DynamicCrudEntityOptions _options;

    internal DynamicCrudEntityBuilder(DynamicCrudEntityOptions options) => _options = options;

    /// <summary>Exposes these properties – readable and, unless they are keys, generated or concurrency tokens, writable.</summary>
    public DynamicCrudEntityBuilder<TEntity> Fields(params Expression<Func<TEntity, object?>>[] fields)
    {
        _options.Fields.AddRange(fields.Select(Name));
        return this;
    }

    /// <summary>
    /// Exposes every mapped scalar property except <paramref name="except"/>. Keys, store-generated values, concurrency tokens and
    /// shadow properties are read-only; navigations are never exposed. Properties added to the entity later are exposed
    /// automatically – prefer <see cref="Fields"/> for entities that may get sensitive columns.
    /// </summary>
    public DynamicCrudEntityBuilder<TEntity> AllFields(params Expression<Func<TEntity, object?>>[] except)
    {
        _options.AllFields = true;
        _options.Except.UnionWith(except.Select(Name));
        return this;
    }

    /// <summary>Exposes these properties read-only (and makes listed ones read-only).</summary>
    public DynamicCrudEntityBuilder<TEntity> ReadOnly(params Expression<Func<TEntity, object?>>[] fields)
    {
        foreach (var name in fields.Select(Name))
        {
            _options.ReadOnly.Add(name);
            if (!_options.Fields.Contains(name))
            {
                _options.Fields.Add(name);
            }
        }

        return this;
    }

    /// <summary>Fields list endpoints may filter on. They must be exposed.</summary>
    public DynamicCrudEntityBuilder<TEntity> Filterable(params Expression<Func<TEntity, object?>>[] fields)
    {
        _options.Filterable.UnionWith(fields.Select(Name));
        return this;
    }

    /// <summary>Fields list endpoints may sort by. They must be exposed.</summary>
    public DynamicCrudEntityBuilder<TEntity> Sortable(params Expression<Func<TEntity, object?>>[] fields)
    {
        _options.Sortable.UnionWith(fields.Select(Name));
        return this;
    }

    /// <summary>The operations definitions may use, e.g. <c>CrudOperations.All &amp; ~CrudOperations.Delete</c>. Default: all.</summary>
    public DynamicCrudEntityBuilder<TEntity> Operations(CrudOperations operations)
    {
        _options.Operations = operations;
        return this;
    }

    /// <summary>
    /// The string property with the tenant of each row (multi-tenancy). Every query is filtered by the endpoint's tenant – a tenant's
    /// endpoints see only their tenant's rows, shared endpoints the rows of the request's tenant – and new rows get it. It is never
    /// writable from a request. Endpoints of every tenant may use the entity, unless <see cref="AllowTenants"/> narrows it.
    /// </summary>
    public DynamicCrudEntityBuilder<TEntity> TenantColumn(Expression<Func<TEntity, string?>> property) =>
        TenantColumn(Name(Expression.Lambda<Func<TEntity, object?>>(property.Body, property.Parameters)));

    /// <summary>The tenant column by name – for a shadow property.</summary>
    public DynamicCrudEntityBuilder<TEntity> TenantColumn(string property)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(property);
        _options.TenantProperty = property;
        return this;
    }

    /// <summary>
    /// Lets the endpoints of <paramref name="tenants"/> use the entity. Without a tenant column the entity is otherwise only for
    /// shared endpoints; with one, this limits it to these tenants.
    /// </summary>
    public DynamicCrudEntityBuilder<TEntity> AllowTenants(params string[] tenants)
    {
        _options.AllowedTenants.UnionWith(tenants);
        return this;
    }

    /// <summary>
    /// Lets the endpoints of every tenant use an entity without a tenant column – they all see the same rows (reference data such as
    /// countries). Don't use it for data that belongs to someone.
    /// </summary>
    public DynamicCrudEntityBuilder<TEntity> SharedAcrossTenants()
    {
        _options.SharedAcrossTenants = true;
        return this;
    }

    private static string Name(Expression<Func<TEntity, object?>> field)
    {
        ArgumentNullException.ThrowIfNull(field);
        var body = field.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } convert ? convert.Operand : field.Body;
        return body is MemberExpression { Member: PropertyInfo property, Expression: ParameterExpression }
            ? property.Name
            : throw new ArgumentException($"'{field}' must select a property of {typeof(TEntity).Name}, e.g. p => p.Name.", nameof(field));
    }
}

/// <summary>An entity as the application configured it; resolved against the EF model by <see cref="DynamicCrudCatalog"/>.</summary>
internal sealed class DynamicCrudEntityOptions(
    Type clrType,
    string name,
    Func<DynamicCrudEntityOptions, Microsoft.EntityFrameworkCore.Metadata.IEntityType, DynamicCrudRegistration, DynamicCrudEntity> create)
{
    public Type ClrType { get; } = clrType;

    public string Name { get; } = name;

    public List<string> Fields { get; } = [];

    public bool AllFields { get; set; }

    public HashSet<string> Except { get; } = new(StringComparer.Ordinal);

    public HashSet<string> ReadOnly { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Filterable { get; } = new(StringComparer.Ordinal);

    public HashSet<string> Sortable { get; } = new(StringComparer.Ordinal);

    public CrudOperations Operations { get; set; } = CrudOperations.All;

    public string? TenantProperty { get; set; }

    public HashSet<string> AllowedTenants { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SharedAcrossTenants { get; set; }

    public DynamicCrudEntity Create(Microsoft.EntityFrameworkCore.Metadata.IEntityType entityType, DynamicCrudRegistration registration) =>
        create(this, entityType, registration);
}

/// <summary>One <c>AddEntityFrameworkCrud&lt;TContext&gt;</c> call.</summary>
internal sealed record DynamicCrudRegistration(Type ContextType, string ProcessorName, IReadOnlyList<DynamicCrudEntityOptions> Entities);
