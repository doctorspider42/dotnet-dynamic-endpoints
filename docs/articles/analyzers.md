# Analyzers

The `DynamicEndpoints` package ships Roslyn analyzers that catch mistakes in the fluent API at build time
instead of when an endpoint is saved. They need no setup and only check compile-time constants
(literals, `const` strings), so routes or rules built at run time are left alone.

| ID | What it reports |
|---|---|
| `DE0001` | `HandledBy<T>()` / `AddProcessor<T>()` with an abstract class, interface or struct |
| `DE0002` | `ValidatedBy<T>()` with a type that is not an `IDynamicValidator` or FluentValidation validator, or is abstract |
| `DE0003` | Two processors with the same name (`[DynamicProcessor]` or derived from the type name) |
| `DE0004` | Two validators with the same name (`[DynamicValidator]` or derived from the type name) |
| `DE0005` | `HandledBy<P>(config)` / `ValidatedBy<V>(config)` with a class that is not the processor's or validator's `TConfiguration` |
| `DE0006` | Invalid route template in `DynamicEndpoint.Get/Post/Put/Patch/Delete("…")` |
| `DE0007` | Invalid regular expression in `p.Pattern("…")` |
| `DE0008` | Regex constructs the non-backtracking engine rejects: backreferences, lookarounds, atomic groups, conditionals |
| `DE0009` | `WithRule("…")` that is not valid JSON |
| `DE0010` | `WithRule("…")` with an unknown JsonLogic operator or an object that is not a single-operator expression |

All rules are warnings. Change the severity or turn a rule off in `.editorconfig`, for example
`dotnet_diagnostic.DE0003.severity = none`.
