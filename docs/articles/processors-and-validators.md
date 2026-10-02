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
