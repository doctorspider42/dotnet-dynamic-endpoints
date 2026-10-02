using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// The operations on one entity type. Queries are built as expression trees from the allowlisted fields – values are always
/// parameters, never SQL or LINQ text – and project to the exposed fields only.
/// </summary>
internal sealed class DynamicCrudEntity<TEntity> : DynamicCrudEntity
    where TEntity : class
{
    private const int MaxInValues = 100;

    private static readonly ParameterExpression Row = Expression.Parameter(typeof(TEntity), "e");
    private static readonly MethodInfo EfProperty = typeof(EF).GetMethod(nameof(EF.Property))!;
    private static readonly MethodInfo ClosureMethod = typeof(DynamicCrudEntity<TEntity>).GetMethod(nameof(Parameterized), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Expression<Func<TEntity, object?[]>> _projection;

    public DynamicCrudEntity(DynamicCrudEntityOptions options, IEntityType entityType, DynamicCrudRegistration registration)
        : base(options, entityType, registration)
    {
        // The exposed fields, then the concurrency tokens for the ETag – nothing else is read.
        var read = Fields.Select(f => f.Property).Concat(ConcurrencyTokens);
        _projection = Expression.Lambda<Func<TEntity, object?[]>>(
            Expression.NewArrayInit(typeof(object), read.Select(p => Expression.Convert(Member(p), typeof(object)))), Row);
    }

    public override Task<IResult> ExecuteAsync(DynamicCrudExecution execution) => execution.Config.Operation switch
    {
        CrudOperation.List => ListAsync(execution),
        CrudOperation.Get => GetAsync(execution),
        CrudOperation.Create => CreateAsync(execution),
        CrudOperation.Update => UpdateAsync(execution, replace: true),
        CrudOperation.Patch => UpdateAsync(execution, replace: false),
        _ => DeleteAsync(execution),
    };

    private async Task<IResult> ListAsync(DynamicCrudExecution x)
    {
        var errors = new List<DynamicValidationError>();
        var request = x.Request;
        var page = request.Parameters["page"] is { } p ? ToInt(p) : 1;
        var pageSize = request.Parameters["pageSize"] is { } s ? ToInt(s) : x.Config.PageSize;
        if (page is not >= 1)
        {
            errors.Add(new("page", DynamicValidationCodes.Minimum, "'page' must be 1 or more."));
        }

        if (pageSize is not >= 1 || pageSize > x.Config.MaxPageSize)
        {
            errors.Add(new("pageSize", DynamicValidationCodes.Maximum, $"'pageSize' must be between 1 and {x.Config.MaxPageSize}."));
        }

        var query = Query(x, tracking: false);
        foreach (var filter in x.Config.Filters ?? [])
        {
            var field = FindField(filter.Field);
            JsonNode? value;
            string key;
            if (filter.Parameter is { } parameter)
            {
                if (!request.Parameters.TryGetPropertyValue(parameter, out value))
                {
                    continue;
                }

                key = SourceName(request, parameter);
            }
            else
            {
                value = filter.Value;
                key = "filters";
            }

            if (field is not { Filterable: true } || !field.Operators.Contains(filter.Operator))
            {
                return DynamicCrudResults.Misconfigured(request, $"Field '{filter.Field}' can't be filtered with '{filter.Operator}' any more.");
            }

            if (Filter(field, filter.Operator, value) is { } predicate)
            {
                query = query.Where(predicate);
            }
            else
            {
                errors.Add(new(key, DynamicValidationCodes.Type, $"'{key}' is not a valid value for {field.Name}."));
            }
        }

        var sort = x.Config.Sort;
        string? sortKey = null;
        if (x.Config.SortParameter is { } sortParameter && request.Parameters[sortParameter] is JsonValue requested &&
            requested.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
        {
            sort = text;
            sortKey = SourceName(request, sortParameter);
        }

        var order = ParseSort(sort, out var sortError);
        if (sortError is not null)
        {
            if (sortKey is null)
            {
                return DynamicCrudResults.Misconfigured(request, sortError);
            }

            errors.Add(new(sortKey, DynamicValidationCodes.NotAllowed, sortError));
        }

        if (errors.Count > 0)
        {
            return DynamicCrudResults.Invalid(request, errors);
        }

        int? total = x.Config.IncludeTotal ? await query.CountAsync(request.RequestAborted) : null;
        var ordered = false;
        foreach (var (field, descending) in order)
        {
            query = Order(query, field.Property, descending, ordered);
            ordered = true;
        }

        // A stable order makes pages deterministic.
        if (KeyField is not null && !order.Any(o => o.Field.Property == KeyField.Property))
        {
            query = Order(query, KeyField.Property, descending: false, ordered);
        }

        var rows = await query.Skip((page!.Value - 1) * pageSize!.Value).Take(pageSize.Value).Select(_projection).ToListAsync(request.RequestAborted);
        var items = new JsonArray(rows.Select(r => (JsonNode)ToJson(r)).ToArray());
        var result = new JsonObject { ["items"] = items, ["page"] = page, ["pageSize"] = pageSize };
        if (total is not null)
        {
            result["total"] = total;
        }

        return Results.Json(result);
    }

    private async Task<IResult> GetAsync(DynamicCrudExecution x)
    {
        if (!TryKey(x, out var key))
        {
            return DynamicCrudResults.NotFound(x.Request);
        }

        var row = await Query(x, tracking: false).Where(KeyEquals(key)).Select(_projection).FirstOrDefaultAsync(x.Request.RequestAborted);
        if (row is null)
        {
            return DynamicCrudResults.NotFound(x.Request);
        }

        SetETag(x.Request, row[Fields.Count..]);
        return Results.Json(ToJson(row));
    }

    private async Task<IResult> CreateAsync(DynamicCrudExecution x)
    {
        var entity = (TEntity)Activator.CreateInstance(typeof(TEntity), nonPublic: true)!;
        var entry = x.Db.Add(entity);
        var errors = Apply(x, entry, creating: true, replace: true);
        if (errors.Count > 0)
        {
            return Discard(entry, DynamicCrudResults.Invalid(x.Request, errors));
        }

        if (Tenant is not null)
        {
            entry.Property(Tenant.Name).CurrentValue = x.Tenant;
        }

        foreach (var token in ConcurrencyTokens.Where(t => t.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never && t.ClrType == typeof(Guid)))
        {
            entry.Property(token.Name).CurrentValue = Guid.NewGuid();
        }

        var context = new DynamicCrudContext<TEntity>(entity, CrudOperation.Create, Name, x.Request, x.Db, x.Tenant);
        var interceptors = x.Request.Services.GetServices<IDynamicCrudInterceptor<TEntity>>().ToList();
        foreach (var interceptor in interceptors)
        {
            await interceptor.BeforeCreateAsync(context);
        }

        if (context.Errors.Count > 0)
        {
            return Discard(entry, DynamicCrudResults.Invalid(x.Request, context.Errors));
        }

        // Interceptors may change anything, but not move the row to another tenant.
        if (Tenant is not null)
        {
            entry.Property(Tenant.Name).CurrentValue = x.Tenant;
        }

        await x.Db.SaveChangesAsync(x.Request.RequestAborted);
        foreach (var interceptor in interceptors)
        {
            await interceptor.AfterCreateAsync(context);
        }

        var values = Read(entry);
        SetETag(x.Request, values[Fields.Count..]);
        var keyText = Convert.ToString(entry.Property(KeyField!.PropertyName).CurrentValue, System.Globalization.CultureInfo.InvariantCulture);
        var http = x.Request.HttpContext.Request;
        return Results.Created($"{http.PathBase}{http.Path.Value!.TrimEnd('/')}/{Uri.EscapeDataString(keyText ?? "")}", ToJson(values));
    }

    private async Task<IResult> UpdateAsync(DynamicCrudExecution x, bool replace)
    {
        if (!TryKey(x, out var key) || await Query(x, tracking: true).Where(KeyEquals(key)).FirstOrDefaultAsync(x.Request.RequestAborted) is not { } entity)
        {
            return DynamicCrudResults.NotFound(x.Request);
        }

        var entry = x.Db.Entry(entity);
        if (Precondition(x, entry) is { } failed)
        {
            return failed;
        }

        var errors = Apply(x, entry, creating: false, replace);
        if (errors.Count > 0)
        {
            return Discard(entry, DynamicCrudResults.Invalid(x.Request, errors));
        }

        var operation = replace ? CrudOperation.Update : CrudOperation.Patch;
        var context = new DynamicCrudContext<TEntity>(entity, operation, Name, x.Request, x.Db, x.Tenant);
        var interceptors = x.Request.Services.GetServices<IDynamicCrudInterceptor<TEntity>>().ToList();
        foreach (var interceptor in interceptors)
        {
            await interceptor.BeforeUpdateAsync(context);
        }

        if (context.Errors.Count > 0)
        {
            return Discard(entry, DynamicCrudResults.Invalid(x.Request, context.Errors));
        }

        if (Tenant is not null)
        {
            entry.Property(Tenant.Name).CurrentValue = x.Tenant;
        }

        RefreshTokens(entry);
        if (await SaveAsync(x) is { } conflict)
        {
            return Discard(entry, conflict);
        }

        foreach (var interceptor in interceptors)
        {
            await interceptor.AfterUpdateAsync(context);
        }

        var values = Read(entry);
        SetETag(x.Request, values[Fields.Count..]);
        return Results.Json(ToJson(values));
    }

    private async Task<IResult> DeleteAsync(DynamicCrudExecution x)
    {
        if (!TryKey(x, out var key) || await Query(x, tracking: true).Where(KeyEquals(key)).FirstOrDefaultAsync(x.Request.RequestAborted) is not { } entity)
        {
            return DynamicCrudResults.NotFound(x.Request);
        }

        var entry = x.Db.Entry(entity);
        if (Precondition(x, entry) is { } failed)
        {
            return failed;
        }

        var context = new DynamicCrudContext<TEntity>(entity, CrudOperation.Delete, Name, x.Request, x.Db, x.Tenant);
        var interceptors = x.Request.Services.GetServices<IDynamicCrudInterceptor<TEntity>>().ToList();
        foreach (var interceptor in interceptors)
        {
            await interceptor.BeforeDeleteAsync(context);
        }

        if (context.Errors.Count > 0)
        {
            return Discard(entry, DynamicCrudResults.Invalid(x.Request, context.Errors));
        }

        entry.State = EntityState.Deleted;
        if (await SaveAsync(x) is { } conflict)
        {
            return Discard(entry, conflict);
        }

        foreach (var interceptor in interceptors)
        {
            await interceptor.AfterDeleteAsync(context);
        }

        return Results.NoContent();
    }

    // Body parameters of the definition → fields. Only exposed, writable fields are set; the definition was checked on save.
    private List<DynamicValidationError> Apply(DynamicCrudExecution x, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry, bool creating, bool replace)
    {
        var errors = new List<DynamicValidationError>();
        foreach (var parameter in x.Request.Endpoint.Parameters.Where(p => p.Source == ParameterSource.Body))
        {
            var field = FindField(parameter.Name);
            if (field is null || !(creating ? field.Creatable : field.Writable))
            {
                // The allowlist changed since the definition was saved: never write it.
                continue;
            }

            if (!x.Request.Parameters.TryGetPropertyValue(parameter.Name, out var node))
            {
                if (!replace || creating)
                {
                    continue;
                }

                node = null;
            }

            if (node is null && !field.IsNullable)
            {
                if (field.ValueType.IsValueType)
                {
                    entry.Property(field.PropertyName).CurrentValue = Activator.CreateInstance(field.ValueType);
                    continue;
                }

                errors.Add(new(parameter.EffectiveSourceName, DynamicValidationCodes.Required, $"'{parameter.EffectiveSourceName}' is required."));
                continue;
            }

            if (!field.TryFromJson(node, out var value))
            {
                errors.Add(new(parameter.EffectiveSourceName, DynamicValidationCodes.Type, $"'{parameter.EffectiveSourceName}' is not a valid {field.Kind.ToString().ToLowerInvariant()}."));
                continue;
            }

            entry.Property(field.PropertyName).CurrentValue = value;
        }

        return errors;
    }

    // If-Match against the current ETag: 412 when it doesn't match, 428 when it is required and missing.
    private IResult? Precondition(DynamicCrudExecution x, Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry)
    {
        var ifMatch = x.Request.HttpContext.Request.Headers.IfMatch;
        if (ifMatch.Count == 0)
        {
            return x.Config.RequireIfMatch && ConcurrencyTokens.Count > 0 ? DynamicCrudResults.PreconditionRequired(x.Request) : null;
        }

        var current = ETag(ConcurrencyTokens.Select(t => entry.Property(t.Name).OriginalValue));
        var tags = ifMatch.SelectMany(v => (v ?? "").Split(',')).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        return tags.Contains("*") || (current is not null && tags.Contains(current, StringComparer.Ordinal))
            ? null
            : DynamicCrudResults.PreconditionFailed(x.Request);
    }

    // A conflict between reading and saving: 412 when the client sent If-Match (its version is gone), else 409.
    private async Task<IResult?> SaveAsync(DynamicCrudExecution x)
    {
        try
        {
            await x.Db.SaveChangesAsync(x.Request.RequestAborted);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return x.Request.HttpContext.Request.Headers.IfMatch.Count > 0
                ? DynamicCrudResults.PreconditionFailed(x.Request)
                : DynamicCrudResults.Conflict(x.Request);
        }
    }

    // Client-side tokens get a new value on every change; store-generated ones (rowversion) are left to the database.
    private void RefreshTokens(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry)
    {
        foreach (var token in ConcurrencyTokens.Where(t => t.ValueGenerated == Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never))
        {
            var property = entry.Property(token.Name);
            property.CurrentValue = (Nullable.GetUnderlyingType(token.ClrType) ?? token.ClrType) switch
            {
                var t when t == typeof(Guid) => Guid.NewGuid(),
                var t when t == typeof(int) => (int)(property.CurrentValue ?? 0) + 1,
                var t when t == typeof(long) => (long)(property.CurrentValue ?? 0L) + 1,
                var t when t == typeof(DateTime) => DateTime.UtcNow,
                var t when t == typeof(DateTimeOffset) => DateTimeOffset.UtcNow,
                _ => property.CurrentValue,
            };
        }
    }

    private bool TryKey(DynamicCrudExecution x, out object? key)
    {
        key = null;
        var name = x.Config.Key ?? KeyField!.Name;
        var node = x.Request.Parameters[name]
            ?? (x.Request.HttpContext.Request.RouteValues.TryGetValue(name, out var raw) && raw is not null ? JsonValue.Create(raw.ToString()) : null);
        if (node is JsonValue value && KeyField!.Kind is not DynamicCrudValueKind.String && value.GetValueKind() == JsonValueKind.String &&
            KeyField.Kind is DynamicCrudValueKind.Integer or DynamicCrudValueKind.Number &&
            decimal.TryParse(value.GetValue<string>(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            node = JsonValue.Create(number);
        }

        return node is not null && KeyField!.TryFromJson(node, out key) && key is not null;
    }

    private IQueryable<TEntity> Query(DynamicCrudExecution x, bool tracking)
    {
        IQueryable<TEntity> query = x.Db.Set<TEntity>();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return Tenant is null ? query : query.Where(Lambda(Expression.Equal(Member(Tenant), Closure(x.Tenant, typeof(string)))));
    }

    private Expression<Func<TEntity, bool>> KeyEquals(object? key) =>
        Lambda(Expression.Equal(Member(KeyField!.Property), Closure(key, KeyField.Property.ClrType)));

    private static Expression<Func<TEntity, bool>>? Filter(DynamicCrudField field, CrudFilterOperator op, JsonNode? value)
    {
        var member = Member(field.Property);
        var type = field.Property.ClrType;
        if (op == CrudFilterOperator.In)
        {
            if (value is not JsonArray array || array.Count > MaxInValues)
            {
                return null;
            }

            var list = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
            foreach (var item in array)
            {
                if (!field.TryFromJson(item, out var converted))
                {
                    return null;
                }

                list.Add(converted);
            }

            var contains = typeof(Enumerable).GetMethods().Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2).MakeGenericMethod(type);
            return Lambda(Expression.Call(contains, Closure(list, list.GetType()), member));
        }

        if (!field.TryFromJson(value, out var operand))
        {
            return null;
        }

        var constant = Closure(operand, type);
        Expression body = op switch
        {
            CrudFilterOperator.Eq => Expression.Equal(member, constant),
            CrudFilterOperator.Ne => Expression.NotEqual(member, constant),
            CrudFilterOperator.Lt => Expression.LessThan(member, constant),
            CrudFilterOperator.Lte => Expression.LessThanOrEqual(member, constant),
            CrudFilterOperator.Gt => Expression.GreaterThan(member, constant),
            CrudFilterOperator.Gte => Expression.GreaterThanOrEqual(member, constant),
            CrudFilterOperator.Contains when operand is string => Expression.Call(member, typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!, constant),
            CrudFilterOperator.StartsWith when operand is string => Expression.Call(member, typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!, constant),
            _ => Expression.Constant(false),
        };
        return Lambda(body);
    }

    private static IQueryable<TEntity> Order(IQueryable<TEntity> query, IProperty property, bool descending, bool thenBy)
    {
        var key = Expression.Lambda(Member(property), Row);
        var method = (thenBy, descending) switch
        {
            (false, false) => nameof(Queryable.OrderBy),
            (false, true) => nameof(Queryable.OrderByDescending),
            (true, false) => nameof(Queryable.ThenBy),
            _ => nameof(Queryable.ThenByDescending),
        };
        return query.Provider.CreateQuery<TEntity>(
            Expression.Call(typeof(Queryable), method, [typeof(TEntity), key.ReturnType], query.Expression, Expression.Quote(key)));
    }

    // A rejected change must not be saved by a later SaveChanges of the same context.
    private static IResult Discard(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry, IResult result)
    {
        if (entry.State == EntityState.Added)
        {
            entry.State = EntityState.Detached;
        }
        else
        {
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
        }

        return result;
    }

    private object?[] Read(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<TEntity> entry) =>
        Fields.Select(f => f.Property).Concat(ConcurrencyTokens).Select(p => entry.Property(p.Name).CurrentValue).ToArray();

    private JsonObject ToJson(object?[] values)
    {
        var item = new JsonObject();
        for (var i = 0; i < Fields.Count; i++)
        {
            item[Fields[i].Name] = Fields[i].ToJson(values[i]);
        }

        return item;
    }

    private void SetETag(DynamicRequest request, IEnumerable<object?> tokens)
    {
        if (ETag(tokens) is { } etag)
        {
            request.HttpContext.Response.Headers.ETag = etag;
        }
    }

    /// <summary>Strong ETag of the concurrency token values: <c>"base64url(json)"</c>; <c>null</c> without tokens.</summary>
    private string? ETag(IEnumerable<object?> tokens)
    {
        if (ConcurrencyTokens.Count == 0)
        {
            return null;
        }

        var json = new JsonArray(tokens.Select(t => t is null ? null : JsonSerializer.SerializeToNode(t, t.GetType())).ToArray()).ToJsonString();
        return $"\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}\"";
    }

    private static string SourceName(DynamicRequest request, string parameter) =>
        request.Endpoint.Parameters.FirstOrDefault(p => p.Name == parameter)?.EffectiveSourceName ?? parameter;

    private static int? ToInt(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number
        : node is JsonValue other && other.GetValueKind() == JsonValueKind.Number && long.TryParse(other.ToJsonString(), out var big) ? (big > int.MaxValue ? int.MaxValue : big < 0 ? 0 : (int)big)
        : null;

    private static Expression Member(IProperty property) =>
        property.PropertyInfo is { } info && !property.IsShadowProperty()
            ? Expression.Property(Row, info)
            : Expression.Call(EfProperty.MakeGenericMethod(property.ClrType), Row, Expression.Constant(property.Name));

    private static Expression<Func<TEntity, bool>> Lambda(Expression body) => Expression.Lambda<Func<TEntity, bool>>(body, Row);

    // Values go into the query as closure members, so EF sends them as parameters (and caches one plan).
    private static Expression Closure(object? value, Type type) => (Expression)ClosureMethod.MakeGenericMethod(type).Invoke(null, [value])!;

    private static Expression Parameterized<T>(T value)
    {
        Expression<Func<T>> lambda = () => value;
        return lambda.Body;
    }
}
