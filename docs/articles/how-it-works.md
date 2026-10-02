# How it works

```mermaid
flowchart LR
    A[HTTP request] --> B{Route match<br/><i>dynamic EndpointDataSource</i>}
    B --> P[Filters: OnRequestAsync<br/><i>access checks</i>]
    P --> C[Binding<br/>route · query · header · body · form]
    C --> D[Type conversion]
    D --> E[JSON Schema<br/>+ parameter validators]
    E --> F[JsonLogic<br/>business rules]
    F --> G[Request validators<br/><i>DB lookups etc.</i>]
    G --> H[IDynamicEndpointProcessor<br/><b>your code</b>]
    H --> I[IResult]
    P -. short-circuit .-> Y[e.g. 403]
    C -. 413 / 415 / bad body .-> X
    E -. errors .-> X[Filters: OnValidationFailedAsync<br/>default: 400 ValidationProblemDetails]
    F -. errors .-> X
    G -. errors .-> X
```

```mermaid
sequenceDiagram
    actor Admin
    participant API as Admin API / your code
    participant M as IDynamicEndpointManager
    participant DB as Store (EF Core)
    participant R as ASP.NET Core routing
    Admin->>API: POST definition
    API->>M: CreateAsync(definition)
    M->>M: compile & validate (schema, regex, rules, processor, conflicts)
    M->>DB: persist
    M->>R: swap endpoint list + fire change token
    R-->>Admin: endpoint is live ⚡
```

- **Routing.** A custom `EndpointDataSource` with a change token. Each change builds a complete new endpoint list and swaps it in with a single assignment.
- **Compile once.** On save, a definition is validated as a whole and compiled: route pattern, schema, regexes, rules, processor and validator configs, validator ↔ parameter type compatibility. A broken definition never reaches the routing table.
- **Built-in engines.** A JSON Schema 2020-12 subset (`type`, `properties`, `required`, `additionalProperties`, `items`, length/range/count limits, `enum`, `const`, `format`, `uniqueItems`, `multipleOf`) and a [JsonLogic](https://jsonlogic.com) evaluator with the reference JavaScript semantics. Unsupported schema keywords and unknown operators are **rejected on save**, never silently ignored.
- **Conflicts.** Detected on save: `/orders/{id}` vs `/Orders/{orderId:int}`, clashes with the app's own endpoints, reserved prefixes.
- **Persistence.** Searchable fields are columns, the full definition is JSON, so the model can grow without migrations. `Revision` is a DB concurrency token.
