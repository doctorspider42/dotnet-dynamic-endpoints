using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class BuiltInProcessorTests
{
    [Fact]
    public async Task Http_forward_escapes_values_adds_headers_and_relays_the_response()
    {
        Environment.SetEnvironmentVariable("DynamicEndpointsTests__ForwardKey", "s3cret");
        var upstream = new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json") };
            response.Headers.Location = new Uri("https://backend.test/orders/42");
            return response;
        });
        await using var host = await StartAsync(upstream, b => b.AddHttpForwardProcessor());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders/{id}").HandledBy("http-forward", new
        {
            url = "https://backend.test/orders/{id}?expand={expand}",
            headers = new Dictionary<string, string> { ["X-Api-Key"] = "{config:DynamicEndpointsTests:ForwardKey}", ["X-Tenant"] = "{tenant}" },
            forwardHeaders = new[] { "Accept-Language" },
        })
            .FromRoute("id")
            .FromQuery("expand")
            .FromHeader("tenant", p => p.BindFrom("X-Tenant-Id"))
            .FromBody("quantity", p => p.Integer()));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders/a%20b?expand=x%26y") { Content = JsonContent.Create(new { quantity = 3 }) };
        request.Headers.Add("Accept-Language", "pl");
        request.Headers.Add("X-Tenant-Id", "{config:DynamicEndpointsTests:ForwardKey}");    // values are never expanded again
        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("""{"ok":true}""", await response.Content.ReadAsStringAsync());
        Assert.Equal("https://backend.test/orders/42", response.Headers.Location!.ToString());

        var sent = upstream.Requests.Single();
        Assert.Equal("https://backend.test/orders/a%20b?expand=x%26y", sent.Url);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("s3cret", sent.Headers["X-Api-Key"]);
        Assert.Equal("{config:DynamicEndpointsTests:ForwardKey}", sent.Headers["X-Tenant"]);
        Assert.Equal("pl", sent.Headers["Accept-Language"]);
        Assert.Equal("""{"quantity":3}""", sent.Body);
    }

    [Fact]
    public async Task Http_forward_maps_responses_and_reports_upstream_failures()
    {
        var upstream = new StubHandler(async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath == "/slow")
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":{"name":"Ada","tags":["a","b"]}}""", Encoding.UTF8, "application/json"),
            };
        });
        await using var host = await StartAsync(upstream, b => b.AddHttpForwardProcessor());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/customers/{id}").HandledBy("http-forward", new
        {
            url = "https://backend.test/customers/{id}",
            responseTemplate = new { id = "{{id}}", name = "{{response.data.name}}", firstTag = "{{response.data.tags[0]}}", upstream = "{{status}}" },
        }).FromRoute("id", p => p.Integer()));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/slow").HandledBy("http-forward", new { url = "https://backend.test/slow", timeoutSeconds = 1 }));

        var mapped = await host.Client.GetFromJsonAsync<JsonObject>("/customers/7");
        Assert.Equal("""{"id":7,"name":"Ada","firstTag":"a","upstream":200}""", mapped!.ToJsonString());

        Assert.Equal(HttpStatusCode.GatewayTimeout, (await host.Client.GetAsync("/slow")).StatusCode);
    }

    [Fact]
    public async Task Http_forward_configuration_is_validated_on_save()
    {
        await using var host = await StartAsync(new StubHandler(_ => new HttpResponseMessage()),
            b => b.AddHttpForwardProcessor(configure: o => o.AllowedHosts.Add("*.internal")));

        async Task<string[]> Errors(object config) =>
            (await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("http-forward", config))).Errors.GetValueOrDefault("processorConfig") ?? [];

        Assert.Empty(await Errors(new { url = "https://api.internal/x" }));
        Assert.Contains(await Errors(new { url = "https://{host}.internal/x" }), e => e.Contains("placeholders in the scheme, host or port"));
        Assert.Contains(await Errors(new { url = "https://evil.example.com/x" }), e => e.Contains("not allowed"));
        Assert.Contains(await Errors(new { url = "ftp://api.internal/x" }), e => e.Contains("absolute http or https"));
        Assert.Contains(await Errors(new { url = "https://api.internal/x", headers = new { A = "{config:Missing:Key}" } }), e => e.Contains("not set"));
        Assert.Contains(await Errors(new { url = "https://api.internal/x", timeoutSecond = 5 }), e => e.Contains("Invalid configuration"));
    }

    [Fact]
    public async Task Webhook_retries_transient_failures_and_signs_the_payload()
    {
        Environment.SetEnvironmentVariable("DynamicEndpointsTests__WebhookSecret", "hook-secret");
        var attempt = 0;
        var upstream = new StubHandler(_ => new HttpResponseMessage(Interlocked.Increment(ref attempt) < 3 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        await using var host = await StartAsync(upstream, b => b.AddWebhookProcessor());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/events").HandledBy("webhook", new
        {
            url = "https://hooks.test/in",
            retryDelayMilliseconds = 1,
            signingSecretConfigurationKey = "DynamicEndpointsTests:WebhookSecret",
            payload = new { type = "order", quantity = "{{quantity}}" },
        }).FromBody("quantity", p => p.Integer().Required()));

        var response = await host.Client.PostAsJsonAsync("/events", new { quantity = 2 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(body!["delivered"]!.GetValue<bool>());
        Assert.Equal(3, body["attempts"]!.GetValue<int>());

        Assert.Equal(3, upstream.Requests.Count);
        Assert.Single(upstream.Requests.Select(r => r.Headers["X-Webhook-Delivery"]).Distinct());
        var sent = upstream.Requests.Last();
        Assert.Equal("""{"type":"order","quantity":2}""", sent.Body);
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes("hook-secret"), Encoding.UTF8.GetBytes(sent.Body!)));
        Assert.Equal(expected, sent.Headers["X-Webhook-Signature"]);
    }

    [Fact]
    public async Task Webhook_gives_up_on_permanent_errors_and_delivers_in_the_background()
    {
        var delivered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new StubHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/background")
            {
                delivered.TrySetResult(request.Content!.ReadAsStringAsync().Result);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });
        await using var host = await StartAsync(upstream, b => b.AddWebhookProcessor());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/rejected").HandledBy("webhook", new { url = "https://hooks.test/rejected", retryDelayMilliseconds = 1 }));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/queued").HandledBy("webhook", new { url = "https://hooks.test/background", background = true })
            .FromBody("id", p => p.Integer()));

        var rejected = await host.Client.PostAsync("/rejected", null);
        Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
        Assert.Single(upstream.Requests);

        var queued = await host.Client.PostAsJsonAsync("/queued", new { id = 5 });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        Assert.True((await queued.Content.ReadFromJsonAsync<JsonObject>())!["queued"]!.GetValue<bool>());
        Assert.Equal("""{"id":5}""", await delivered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Response_template_renders_json_and_text()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddResponseTemplateProcessor());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/mock/{id}").HandledBy("response", new
        {
            statusCode = 201,
            body = new { id = "{{id}}", label = "Order {{id}}", tags = new[] { "{{tag}}" }, missing = "{{nope}}" },
            headers = new { X_Order = "{id}" },
        }).FromRoute("id", p => p.Integer()).FromQuery("tag", p => p.Default("none")));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/hello").HandledBy("response", new { text = "Hello, {{name}}!" }).FromQuery("name"));

        var json = await host.Client.GetAsync("/mock/12");
        Assert.Equal(HttpStatusCode.Created, json.StatusCode);
        Assert.Equal("""{"id":12,"label":"Order 12","tags":["none"],"missing":null}""", await json.Content.ReadAsStringAsync());
        Assert.Equal("12", json.Headers.GetValues("X_Order").Single());

        Assert.Equal("Hello, Ada!", await host.Client.GetStringAsync("/hello?name=Ada"));

        var invalid = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("response", new { text = "a", body = new { } }));
        Assert.Contains("processorConfig", invalid.Errors.Keys);
    }

    [Fact]
    public async Task Sql_query_binds_parameters_and_rejects_writes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"de-sql-{Guid.NewGuid():N}.db");
        await using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await setup.OpenAsync();
            await using var command = setup.CreateCommand();
            command.CommandText = """
                CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT, country TEXT);
                INSERT INTO customers VALUES (1, 'Ada', 'PL'), (2, 'Bob', 'DE'), (3, 'Cy', 'PL');
                """;
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await using var host = await TestHost.StartAsync(configure: b =>
                b.AddSqlQueryProcessor(_ => new SqliteConnection($"Data Source={path};Pooling=False")));
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/customers").HandledBy("sql-query", new
            {
                query = "SELECT id, name FROM customers WHERE country = @country ORDER BY id -- 'DROP' in a comment is fine",
                maxRows = 10,
            }).FromQuery("country", p => p.Required()));
            await host.Manager.CreateAsync(DynamicEndpoint.Get("/customers/{id}").HandledBy("sql-query", new
            {
                query = "SELECT id, name FROM customers WHERE id = @id;",
                result = "Row",
            }).FromRoute("id", p => p.Integer()));

            Assert.Equal("""[{"id":1,"name":"Ada"},{"id":3,"name":"Cy"}]""", await host.Client.GetStringAsync("/customers?country=PL"));
            Assert.Equal("[]", await host.Client.GetStringAsync("/customers?country=" + Uri.EscapeDataString("PL' OR '1'='1")));
            Assert.Equal("""{"id":2,"name":"Bob"}""", await host.Client.GetStringAsync("/customers/2"));
            Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/customers/9")).StatusCode);

            async Task<string[]> Errors(string query) =>
                (await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("sql-query", new { query }))).Errors.GetValueOrDefault("processorConfig") ?? [];

            Assert.Empty(await Errors("SELECT 'DELETE FROM x; DROP' AS text"));
            Assert.Contains(await Errors("DELETE FROM customers"), e => e.Contains("Only SELECT"));
            Assert.Contains(await Errors("SELECT 1; DROP TABLE customers"), e => e.Contains("single statement"));
            Assert.Contains(await Errors("SELECT * INTO copy FROM customers"), e => e.Contains("'INTO'"));
            Assert.Contains(await Errors("WITH x AS (SELECT 1) UPDATE customers SET name = 'x'"), e => e.Contains("'UPDATE'"));
            Assert.Contains(await Errors("SELECT 'a\\' ; DELETE FROM customers; --'"), e => e.Contains("single statement"));
            Assert.Contains(await Errors("SELECT 1 /*! ; DROP TABLE customers */"), e => e.Contains("Executable comments"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Task<TestHost> StartAsync(StubHandler upstream, Action<IDynamicEndpointsBuilder> configure) =>
        TestHost.StartAsync(configure: b =>
        {
            configure(b);
            b.Services.AddHttpClient("DynamicEndpoints").ConfigurePrimaryHttpMessageHandler(() => upstream);
        });

    internal sealed record SentRequest(HttpMethod Method, string Url, Dictionary<string, string> Headers, string? Body);

    internal sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = (r, _) => Task.FromResult(respond(r));

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public ConcurrentQueue<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Enqueue(new SentRequest(request.Method, request.RequestUri!.AbsoluteUri, headers, body));
            return await _respond(request, cancellationToken);
        }
    }
}
