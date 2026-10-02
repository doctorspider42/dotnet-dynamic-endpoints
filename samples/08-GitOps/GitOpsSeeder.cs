using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.GitOps;

/// <summary>
/// "What's running on the server" before the first push: it differs from <c>endpoints.yaml</c> on purpose, so a diff has
/// something to show – <c>GET /orders/{id}</c> has another limit and status, <c>GET /legacy</c> isn't in the file (a sync deletes
/// it), <c>POST /orders</c> and <c>GET /health</c> are missing (a push creates them).
/// </summary>
public sealed class GitOpsSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        await manager.CreateAsync(DynamicEndpoint.Get("/orders/{id}")
            .Named("Get order")
            .InGroup("Orders")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["id"] = "{{id}}", ["status"] = "pending" } })
            .FromRoute("id", p => p.Integer().Range(1, 1000)), cancellationToken);

        await manager.CreateAsync(DynamicEndpoint.Get("/legacy")
            .Named("Legacy endpoint")
            .InGroup("Legacy")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Text = "Still here – until the next push --sync." }), cancellationToken);
    }
}
