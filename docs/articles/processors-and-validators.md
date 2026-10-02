# Processors, validators, seeders & assembly scanning

```csharp
builder.Services.AddDynamicEndpoints()
    .AddFromAssemblyContaining<Program>()                    // everything at once…
    .AddProcessorsFromAssembly(typeof(Program).Assembly, t => t.Namespace != "Legacy")   // …or filtered
    .AddProcessor<OrderProcessor>("orders-v2")               // explicit name (scanning then skips the type)
    .AddProcessor("ping", r => Results.Ok("pong"))           // inline
    .AddValidator("even", ctx => { /* … */ return ValueTask.CompletedTask; }, DynamicValidatorTargets.Parameter)
    .AddSeeder<OrdersSeeder>();
```

- **Names:** come from `[DynamicProcessor("…")]` / `[DynamicValidator("…")]` or the type name (`OrderLookupProcessor` → `order-lookup`).
- **Idempotent scanning:** a type that is already registered is skipped.
- **Single entry point:** register one processor and set `options.DefaultProcessor`.
- **Configuration checks:** a typed processor overrides `Validate(config)` to reject bad configuration on save. For checks that
  depend on the definition, such as its tenant, override `Validate(config, definition)` instead (untyped processors:
  `IDynamicEndpointProcessor.ValidateConfiguration(configuration, definition)`). The library calls the definition overload; by
  default it calls the old one, so existing processors keep working.

```csharp
protected override IEnumerable<string> Validate(ReportConfig config, DynamicEndpointDefinition definition)
{
    if (config.Premium && definition.Tenant is not null && !premiumTenants.Contains(definition.Tenant))
        yield return $"Premium reports aren't available to tenant '{definition.Tenant}'.";
}
```

- **Ready-made processors:** HTTP forward, webhook, response templates and read-only SQL are opt-in, see
  [Built-in processors](built-in-processors.md).
