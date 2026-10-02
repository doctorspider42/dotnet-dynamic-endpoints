// The showcase app (samples/Showcase) on PostgreSQL (definitions, history, drafts and the "collection" documents) and Redis
// (instant propagation of changes), in two replicas – edit an endpoint in the panel and watch both pick it up. Metrics and
// traces, including the per-endpoint dynamic_endpoints.* instruments, go to the Aspire dashboard.
//
//   dotnet run --project samples/DynamicEndpoints.AppHost      (needs Docker or Podman for PostgreSQL and Redis)
//
// The focused samples (samples/01-QuickStart … samples/11-Observability) run without Docker; 11-Observability shows the
// telemetry setup on its own, with a console exporter.

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres").WithDataVolume();
var database = postgres.AddDatabase("dynamicendpoints");

var redis = builder.AddRedis("redis");

builder.AddProject<Projects.DynamicEndpoints_Samples_Showcase>("showcase")
    .WithReference(database)
    .WaitFor(database)
    .WithReference(redis)
    .WaitFor(redis)
    .WithReplicas(2)
    .WithExternalHttpEndpoints()
    .WithUrlForEndpoint("http", url =>
    {
        url.Url = "/admin/";
        url.DisplayText = "Admin panel";
    })
    .WithHttpHealthCheck("/health");

builder.Build().Run();
