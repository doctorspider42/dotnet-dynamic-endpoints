# DynamicEndpoints.Yaml

YAML support for [DynamicEndpoints](https://github.com/doctorspider42/dotnet-dynamic-endpoints): export and import endpoint
definitions as YAML (handy for GitOps reviews), and import OpenAPI documents written in YAML. Built on
[YamlDotNet](https://github.com/aaubry/YamlDotNet).

```csharp
builder.Services.AddDynamicEndpoints().AddYamlFormat();
```

The admin API then speaks YAML too:

```bash
curl https://api.example.com/api/admin/endpoints/export?format=yaml > endpoints.yaml
curl -X POST "https://api.example.com/api/admin/endpoints/import?mode=sync&dryRun=true" \
  -H "Content-Type: application/yaml" --data-binary @endpoints.yaml
curl -X POST "https://api.example.com/api/admin/endpoints/import/openapi?dryRun=true" \
  -H "Content-Type: application/yaml" --data-binary @petstore.yaml
```

```yaml
format: dynamic-endpoints/v1
endpoints:
  - id: 0199a4f2-3c1e-7b4a-9d2e-5f6a7b8c9d0e
    method: GET
    route: /orders/{id}
    processor: orders
    parameters:
      - name: id
        source: Route
        type: Integer
        required: true
```

In code, `DynamicEndpointsYaml.Parse(yaml)` and `DynamicEndpointsYaml.Write(node)` convert between YAML and `JsonNode`. Plain
scalars follow the YAML 1.2 core schema, and strings that would read as something else (`"true"`, `"007"`) are written quoted, so
a round trip never changes a type. The `dynamic-endpoints` CLI reads and writes `.yaml` files with this package.
