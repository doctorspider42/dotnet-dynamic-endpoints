using Microsoft.EntityFrameworkCore;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// Application code around the writes of <c>ef-crud</c> endpoints on <typeparamref name="TEntity"/> – defaults, server-side values,
/// business checks, side effects. Register in DI (<c>services.AddScoped&lt;IDynamicCrudInterceptor&lt;Product&gt;, ProductRules&gt;()</c>);
/// all registered interceptors run, in registration order, in the request scope. <c>Before…</c> runs before <c>SaveChanges</c> and may
/// change the entity (any property, not only exposed ones – but not the tenant column) or reject the request with
/// <see cref="DynamicCrudContext{TEntity}.Reject"/>; <c>After…</c> runs once the change is saved. Update covers PUT and PATCH.
/// These are about data; changes of endpoint definitions are reported by change handlers and the audit log.
/// </summary>
public interface IDynamicCrudInterceptor<TEntity>
    where TEntity : class
{
    ValueTask BeforeCreateAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;

    ValueTask AfterCreateAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;

    ValueTask BeforeUpdateAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;

    ValueTask AfterUpdateAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;

    ValueTask BeforeDeleteAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;

    ValueTask AfterDeleteAsync(DynamicCrudContext<TEntity> context) => ValueTask.CompletedTask;
}

/// <summary>A write of an <c>ef-crud</c> endpoint, as seen by <see cref="IDynamicCrudInterceptor{TEntity}"/>.</summary>
public sealed class DynamicCrudContext<TEntity>
    where TEntity : class
{
    private readonly List<DynamicValidationError> _errors = [];

    internal DynamicCrudContext(TEntity entity, CrudOperation operation, string entityName, DynamicRequest request, DbContext dbContext, string? tenant)
    {
        Entity = entity;
        Operation = operation;
        EntityName = entityName;
        Request = request;
        DbContext = dbContext;
        Tenant = tenant;
    }

    /// <summary>The entity with the request's values applied (create, update) or about to be removed (delete). Tracked by <see cref="DbContext"/>.</summary>
    public TEntity Entity { get; }

    /// <summary><see cref="CrudOperation.Create"/>, <see cref="CrudOperation.Update"/>, <see cref="CrudOperation.Patch"/> or <see cref="CrudOperation.Delete"/>.</summary>
    public CrudOperation Operation { get; }

    /// <summary>The exposed name, e.g. <c>products</c>.</summary>
    public string EntityName { get; }

    public DynamicRequest Request { get; }

    public DbContext DbContext { get; }

    /// <summary>Tenant the rows are filtered by (entities with a tenant column), else <c>null</c>.</summary>
    public string? Tenant { get; }

    public CancellationToken CancellationToken => Request.RequestAborted;

    /// <summary>Errors added with <see cref="Reject"/>.</summary>
    public IReadOnlyList<DynamicValidationError> Errors => _errors;

    /// <summary>Rejects the request with a <c>400</c> validation error (in a <c>Before…</c> method; nothing is saved).</summary>
    public void Reject(string field, string message, string code = DynamicValidationCodes.Custom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        _errors.Add(new DynamicValidationError(field, code, message));
    }
}
