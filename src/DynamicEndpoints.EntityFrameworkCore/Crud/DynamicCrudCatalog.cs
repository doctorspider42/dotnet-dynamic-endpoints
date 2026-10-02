using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DynamicEndpoints.EntityFrameworkCore;

/// <summary>
/// The exposed entities of every <c>AddEntityFrameworkCrud</c> call, resolved against the EF model on first use (at the latest when
/// the application starts). A configuration that doesn't match the model fails then, with all problems at once.
/// </summary>
internal sealed class DynamicCrudCatalog
{
    private readonly IReadOnlyList<DynamicCrudRegistration> _registrations;
    private readonly Lazy<Dictionary<string, DynamicCrudEntity>> _entities;

    public DynamicCrudCatalog(IEnumerable<DynamicCrudRegistration> registrations, IServiceScopeFactory scopeFactory)
    {
        _registrations = registrations.ToList();
        _entities = new(() => Build(_registrations, scopeFactory));
    }

    public IReadOnlyList<DynamicCrudRegistration> Registrations => _registrations;

    public IEnumerable<DynamicCrudEntity> All => _entities.Value.Values.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase);

    public DynamicCrudEntity? Find(string? name) =>
        name is not null && _entities.Value.TryGetValue(name, out var entity) ? entity : null;

    /// <summary>The entities the endpoints of <paramref name="tenant"/> (<c>null</c>: shared endpoints) may use.</summary>
    public IEnumerable<DynamicCrudEntity> AvailableTo(string? tenant) => All.Where(e => e.IsAvailableTo(tenant));

    public bool IsProcessor(string? name) =>
        name is not null && _registrations.Any(r => string.Equals(r.ProcessorName, name, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, DynamicCrudEntity> Build(IReadOnlyList<DynamicCrudRegistration> registrations, IServiceScopeFactory scopeFactory)
    {
        var entities = new Dictionary<string, DynamicCrudEntity>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        using var scope = scopeFactory.CreateScope();
        foreach (var registration in registrations)
        {
            var context = (DbContext)scope.ServiceProvider.GetRequiredService(registration.ContextType);
            foreach (var options in registration.Entities)
            {
                if (context.Model.FindEntityType(options.ClrType) is not { } entityType || entityType.IsOwned())
                {
                    errors.Add($"'{options.Name}': {options.ClrType.Name} is not an entity of {registration.ContextType.Name}.");
                    continue;
                }

                var entity = options.Create(entityType, registration);
                errors.AddRange(entity.Errors.Select(e => $"'{options.Name}' ({options.ClrType.Name}): {e}"));
                if (!entities.TryAdd(entity.Name, entity))
                {
                    errors.Add($"The entity name '{entity.Name}' is used by more than one AddEntityFrameworkCrud registration.");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("The ef-crud configuration doesn't match the EF model:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(e => "- " + e)));
        }

        return entities;
    }
}

/// <summary>Resolves the catalog when the application starts, so a broken configuration fails right away and not on first use.</summary>
internal sealed class DynamicCrudStartupCheck(DynamicCrudCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = catalog.All.Count();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
