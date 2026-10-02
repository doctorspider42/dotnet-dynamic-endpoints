using System.Net.Mail;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Benchmarks;

/// <summary>
/// Full request round trips (HttpClient → TestServer → response body) of a dynamic endpoint vs the equivalent
/// hand-written minimal API, both hosted in the same application. The minimal API is the baseline of each category.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class RequestBenchmarks
{
    private static readonly byte[] ValidJson = Encoding.UTF8.GetBytes("""{"name":"Widget","quantity":5,"email":"jane@example.com"}""");
    private static readonly byte[] InvalidJson = Encoding.UTF8.GetBytes($$"""{"name":"{{new string('x', 60)}}","quantity":0,"email":"not-an-email"}""");
    private static readonly KeyValuePair<string, string>[] Form = [new("name", "Widget"), new("quantity", "5"), new("subscribe", "true")];

    private BenchmarkHost _host = null!;
    private HttpClient _client = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _host = await BenchmarkHost.StartAsync(
            b => b
                .AddProcessor("item", r => Results.Ok(new { id = r.Get<int>("id") }))
                .AddProcessor("order", r => Results.Ok(new { name = r.Get<string>("name"), quantity = r.Get<int>("quantity"), email = r.Get<string>("email") }))
                .AddProcessor("form", r => Results.Ok(new { name = r.Get<string>("name"), quantity = r.Get<int>("quantity"), subscribe = r.Get<bool>("subscribe") })),
            app =>
            {
                app.MapGet("/static/items/{id:int}", (int id) => Results.Ok(new { id }));
                app.MapPost("/static/orders", (OrderRequest order) => ValidateOrder(order) is { } errors
                    ? Results.ValidationProblem(errors)
                    : Results.Ok(new { name = order.Name, quantity = order.Quantity, email = order.Email }));
                app.MapPost("/static/forms", ([FromForm] FormRequest form) => Results.Ok(new { name = form.Name, quantity = form.Quantity, subscribe = form.Subscribe }))
                    .DisableAntiforgery();
            });

        await _host.Manager.UpsertAsync(DynamicEndpoint.Get("/items/{id}").HandledBy("item").FromRoute("id", p => p.Integer()));
        await _host.Manager.UpsertAsync(DynamicEndpoint.Post("/orders").HandledBy("order")
            .FromBody("name", p => p.String().Required().MaxLength(50))
            .FromBody("quantity", p => p.Integer().Required().Range(1, 100))
            .FromBody("email", p => p.Email()));
        await _host.Manager.UpsertAsync(DynamicEndpoint.Post("/forms").HandledBy("form")
            .FromForm("name", p => p.String().Required().MaxLength(50))
            .FromForm("quantity", p => p.Integer().Required().Range(1, 100))
            .FromForm("subscribe", p => p.Boolean()));
        _client = _host.Client;

        // Fail fast when an endpoint doesn't behave as expected (the benchmarks check the status too).
        await Static_Get();
        await Dynamic_Get();
        await Static_PostJson();
        await Dynamic_PostJson();
        await Static_PostJsonInvalid();
        await Dynamic_PostJsonInvalid();
        await Static_PostForm();
        await Dynamic_PostForm();
    }

    [GlobalCleanup]
    public async Task CleanupAsync() => await _host.DisposeAsync();

    [Benchmark(Baseline = true, Description = "Minimal API"), BenchmarkCategory("GET /items/{id}")]
    public async Task<int> Static_Get() => await (await _client.GetAsync("/static/items/42")).ReadAsync(200);

    [Benchmark(Description = "Dynamic endpoint"), BenchmarkCategory("GET /items/{id}")]
    public async Task<int> Dynamic_Get() => await (await _client.GetAsync("/items/42")).ReadAsync(200);

    [Benchmark(Baseline = true, Description = "Minimal API"), BenchmarkCategory("POST JSON (valid)")]
    public async Task<int> Static_PostJson() => await (await _client.PostAsync("/static/orders", Json(ValidJson))).ReadAsync(200);

    [Benchmark(Description = "Dynamic endpoint"), BenchmarkCategory("POST JSON (valid)")]
    public async Task<int> Dynamic_PostJson() => await (await _client.PostAsync("/orders", Json(ValidJson))).ReadAsync(200);

    [Benchmark(Baseline = true, Description = "Minimal API"), BenchmarkCategory("POST JSON (invalid, 400)")]
    public async Task<int> Static_PostJsonInvalid() => await (await _client.PostAsync("/static/orders", Json(InvalidJson))).ReadAsync(400);

    [Benchmark(Description = "Dynamic endpoint"), BenchmarkCategory("POST JSON (invalid, 400)")]
    public async Task<int> Dynamic_PostJsonInvalid() => await (await _client.PostAsync("/orders", Json(InvalidJson))).ReadAsync(400);

    [Benchmark(Baseline = true, Description = "Minimal API"), BenchmarkCategory("POST form")]
    public async Task<int> Static_PostForm() => await (await _client.PostAsync("/static/forms", new FormUrlEncodedContent(Form))).ReadAsync(200);

    [Benchmark(Description = "Dynamic endpoint"), BenchmarkCategory("POST form")]
    public async Task<int> Dynamic_PostForm() => await (await _client.PostAsync("/forms", new FormUrlEncodedContent(Form))).ReadAsync(200);

    private static ByteArrayContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new("application/json");
        return content;
    }

    // Hand-written equivalent of the dynamic endpoint's rules.
    private static Dictionary<string, string[]>? ValidateOrder(OrderRequest order)
    {
        Dictionary<string, string[]>? errors = null;
        void Add(string key, string message) => (errors ??= [])[key] = [message];

        if (string.IsNullOrEmpty(order.Name))
        {
            Add("name", "The name field is required.");
        }
        else if (order.Name.Length > 50)
        {
            Add("name", "The name field must be at most 50 characters long.");
        }

        if (order.Quantity is null)
        {
            Add("quantity", "The quantity field is required.");
        }
        else if (order.Quantity is < 1 or > 100)
        {
            Add("quantity", "The quantity field must be between 1 and 100.");
        }

        if (order.Email is not null && !MailAddress.TryCreate(order.Email, out _))
        {
            Add("email", "The email field must be a valid e-mail address.");
        }

        return errors;
    }
}

public sealed record OrderRequest(string? Name, int? Quantity, string? Email);

public sealed record FormRequest(string Name, int Quantity, bool Subscribe);
