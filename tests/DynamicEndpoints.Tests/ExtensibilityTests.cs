using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class ExtensibilityTests
{
    [Fact]
    public async Task Request_filter_runs_before_validation_and_can_short_circuit()
    {
        var processed = 0;
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddProcessor("count", request =>
            {
                processed++;
                return Results.Ok();
            })
            .AddFilter<FeatureAccessFilter>());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/reports").HandledBy("count", new { feature = "reports" })
            .FromBody("from", p => p.Date().Required()));

        var forbidden = await Send(host, "/reports", tenant: "free", json: "{}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var invalid = await Send(host, "/reports", tenant: "pro", json: "{}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        var ok = await Send(host, "/reports", tenant: "pro", json: """{ "from": "2026-10-01" }""");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, processed);
    }

    [Fact]
    public async Task Validation_failures_go_through_filters_in_a_custom_format()
    {
        var log = new List<string>();
        await using var host = await TestHost.StartAsync(
            options: o => o.MaxRequestBodySize = 64,
            configure: b => b
                .AddFilter(onValidationFailed: c =>
                {
                    log.Add($"{c.Endpoint.Name}:{c.Reason}:{c.StatusCode}");
                    return ValueTask.CompletedTask;
                })
                .AddFilter<ApiErrorFormatFilter>());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/orders").Named("Create order").HandledBy("echo")
            .FromBody("quantity", p => p.Integer().Required().Min(1))
            .FromBody("note", p => p.MaxLength(3))
            .WithRule("""{ "!=": [{ "var": "quantity" }, 13] }""", "Unlucky.", "quantity", code: "unlucky"));

        var invalid = await (await Send(host, "/orders", json: """{ "quantity": 0, "note": "toolong" }""")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("1.0", invalid!["apiVersion"]!.GetValue<string>());
        Assert.Equal("VALIDATION_FAILED", invalid["error"]!["code"]!.GetValue<string>());
        var details = invalid["error"]!["details"]!.AsArray();
        Assert.Contains(details, d => d!["field"]!.GetValue<string>() == "quantity" && d["code"]!.GetValue<string>() == "minimum");
        Assert.Contains(details, d => d!["field"]!.GetValue<string>() == "note" && d["code"]!.GetValue<string>() == "maxLength");

        var rule = await (await Send(host, "/orders", json: """{ "quantity": 13 }""")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("unlucky", rule!["error"]!["details"]![0]!["code"]!.GetValue<string>());

        var malformed = await Send(host, "/orders", json: "{oops");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("INVALIDBODY", (await malformed.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>());

        var tooLarge = await Send(host, "/orders", json: $$"""{ "note": "{{new string('x', 100)}}" }""");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);

        var wrongType = await host.Client.PostAsync("/orders", new StringContent("quantity=1", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);
        Assert.Equal("UNSUPPORTEDMEDIATYPE", (await wrongType.Content.ReadFromJsonAsync<JsonObject>())!["error"]!["code"]!.GetValue<string>());

        Assert.Equal(
            ["Create order:Validation:400", "Create order:Validation:400", "Create order:InvalidBody:400", "Create order:PayloadTooLarge:413", "Create order:UnsupportedMediaType:415"],
            log);
    }

    [Theory]
    [InlineData(new byte[] { 0x7B, 0x22, 0x6E, 0x22, 0x3A, 0x22, 0xFF, 0x22, 0x7D })] // {"n":"<0xFF>"}
    [InlineData(new byte[] { 0x7B, 0x22, 0xC3, 0x22, 0x3A, 0x31, 0x7D })] // truncated sequence in a property name
    [InlineData(new byte[] { 0x7B, 0x22, 0x6E, 0x22, 0x3A, 0x22, 0x5C, 0x75, 0x64, 0x38, 0x30, 0x30, 0x22, 0x7D })] // {"n":"\ud800"}
    [InlineData(new byte[] { 0x7B, 0x22, 0x5C, 0x75, 0x64, 0x63, 0x30, 0x30, 0x22, 0x3A, 0x31, 0x7D })] // {"\udc00":1}
    public async Task Invalid_text_encoding_is_a_bad_request_not_a_server_error(byte[] body)
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/u").HandledBy("echo").FromBody("n", p => p.MinLength(1)));
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        var errors = await RequestHandlingTests.ReadErrorsAsync(await host.Client.PostAsync("/u", content));

        Assert.Equal("The request body is not valid UTF-8.", errors["body"].Single());
    }

    [Fact]
    public async Task Endpoint_metadata_carries_the_complete_definition()
    {
        DynamicEndpointMetadata? seen = null;
        await using var host = await TestHost.StartAsync(configureApp: app => app.Use((context, next) =>
        {
            seen = context.GetEndpoint()?.Metadata.GetMetadata<DynamicEndpointMetadata>();
            return next(context);
        }));
        var created = await host.Manager.CreateAsync(DynamicEndpoint.Post("/files").Named("Upload").HandledBy("echo")
            .FromForm("document", p => p.File())
            .FromForm("title"));

        await host.Client.PostAsync("/files", new MultipartFormDataContent { { new StringContent("x"), "title" } });

        Assert.NotNull(seen);
        Assert.Equal(created.Id, seen.Id);
        Assert.Equal(1, seen.Revision);
        Assert.Equal("echo", seen.ProcessorName);
        Assert.True(seen.HasFiles);
        Assert.True(seen.HasFormBody);
        Assert.False(seen.HasJsonBody);
        Assert.Equal(ParameterType.File, seen.FindParameter("document")!.Type);
    }

    [Fact]
    public async Task Messages_can_be_localized_overridden_and_routed_through_a_localizer()
    {
        await using var polish = await TestHost.StartAsync(options: o =>
        {
            o.Messages.DefaultCulture = "pl";
            o.Messages.Set("pl", "required.header", "Brak nagłówka {0}.");
        });
        await polish.Manager.CreateAsync(DynamicEndpoint.Get("/search").HandledBy("echo")
            .FromQuery("a", p => p.MinLength(2))
            .FromQuery("b", p => p.MinLength(5))
            .FromQuery("c", p => p.MinLength(22))
            .FromQuery("limit", p => p.Integer())
            .FromHeader("tenant", p => p.BindFrom("X-Tenant").Required()));

        var errors = await RequestHandlingTests.ReadErrorsAsync(await polish.Client.GetAsync("/search?a=x&b=x&c=x&limit=ten"));

        Assert.Equal("Musi mieć co najmniej 2 znaki.", errors["a"].Single());
        Assert.Equal("Musi mieć co najmniej 5 znaków.", errors["b"].Single());
        Assert.Equal("Musi mieć co najmniej 22 znaki.", errors["c"].Single());
        Assert.Equal("Wartość 'ten' nie jest poprawną liczbą całkowitą.", errors["limit"].Single());
        Assert.Equal("Brak nagłówka X-Tenant.", errors["X-Tenant"].Single());

        var contexts = new List<DynamicValidationMessageContext>();
        await using var custom = await TestHost.StartAsync(
            options: o =>
            {
                o.Messages.UseRequestCulture = true;
                o.Messages.Localizer = c =>
                {
                    contexts.Add(c);
                    return c.Key == "minLength" ? $"[{c.Culture.Name}] min {c.Arguments[0]}" : null;
                };
            },
            configureApp: app => app.Use((context, next) =>
            {
                // What app.UseRequestLocalization() does.
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(context.Request.Headers.AcceptLanguage.ToString());
                return next(context);
            }));
        await custom.Manager.CreateAsync(DynamicEndpoint.Get("/search").HandledBy("echo").FromQuery("a", p => p.MinLength(2)));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/search?a=x");
        request.Headers.AcceptLanguage.ParseAdd("de-DE");
        var localized = await RequestHandlingTests.ReadErrorsAsync(await custom.Client.SendAsync(request));
        Assert.Equal("[de-DE] min 2", localized["a"].Single());

        var context = contexts.First(c => c.Key == "minLength");
        Assert.Equal(DynamicValidationCodes.MinLength, context.Code);
        Assert.Equal(2, context.Count);
        Assert.Equal("Must be at least 2 characters long.", context.DefaultMessage);
    }

    [Theory]
    [InlineData("/api/v1/orders", true)]
    [InlineData("/API/V12/orders/{id}", true)]
    [InlineData("/api/v2", true)]
    [InlineData("/orders", false)]
    [InlineData("/api/vx/orders", false)]
    [InlineData("/api/v1x/orders", false)]
    [InlineData("/api/v{version}/orders", false)]
    public async Task Required_route_prefixes_are_enforced(string route, bool allowed)
    {
        await using var host = await TestHost.StartAsync(options: o => o.RequiredRoutePrefixes.Add("/api/v{version:int}"));

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get(route).HandledBy("echo").FromRoute("id").FromRoute("version"));

        Assert.Equal(allowed, !result.Errors.TryGetValue("route", out var errors) || !errors.Any(e => e.StartsWith("Routes must start with")));
    }

    [Fact]
    public async Task OpenApi_document_can_be_extended_with_security_and_common_headers()
    {
        await using var host = await TestHost.StartAsync(options: o =>
        {
            o.OpenApi.AddApiKey("X-Api-Key", description: "Tenant key");
            o.OpenApi.AddHeader("X-End-User", "End user on whose behalf the call is made.");
            o.OpenApi.AddHeader("Idempotency-Key", "Makes retries safe.", appliesTo: d => d.Method != "GET");
            o.OpenApi.ConfigureOperation = (operation, d) => operation["x-feature"] = d.Group;
            o.OpenApi.ConfigureDocument = document => document["servers"] = new JsonArray(new JsonObject { ["url"] = "https://api.example.com" });
        });
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/items").InGroup("catalog").HandledBy("echo"));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/items").HandledBy("echo").FromHeader("user", p => p.BindFrom("X-End-User").Required()));

        var document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");

        Assert.Equal("apiKey", document!["components"]!["securitySchemes"]!["ApiKey"]!["type"]!.GetValue<string>());
        Assert.Equal("X-Api-Key", document["components"]!["securitySchemes"]!["ApiKey"]!["name"]!.GetValue<string>());
        Assert.NotNull(document["security"]![0]!["ApiKey"]);
        Assert.Equal("https://api.example.com", document["servers"]![0]!["url"]!.GetValue<string>());

        var get = document["paths"]!["/items"]!["get"]!;
        Assert.Equal(["X-End-User"], get["parameters"]!.AsArray().Select(p => p!["name"]!.GetValue<string>()));
        Assert.Equal("catalog", get["x-feature"]!.GetValue<string>());

        var post = document["paths"]!["/items"]!["post"]!["parameters"]!.AsArray();
        Assert.Equal(["X-End-User", "Idempotency-Key"], post.Select(p => p!["name"]!.GetValue<string>()));
        Assert.True(post[0]!["required"]!.GetValue<bool>()); // the endpoint's own definition wins
    }

    [Fact]
    public async Task Revision_replaces_version_but_old_payloads_still_work()
    {
        await using var host = await TestHost.StartAsync();
        var create = await host.Client.PostAsJsonAsync("/admin/endpoints", new { method = "GET", route = "/r", processor = "echo" });
        var created = await create.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, created!["revision"]!.GetValue<int>());
        Assert.False(created.ContainsKey("version"));

        // A 0.1.x client still sends "version".
        var legacy = new JsonObject { ["method"] = "GET", ["route"] = "/r", ["processor"] = "echo", ["version"] = 1, ["name"] = "legacy" };
        var updated = await host.Client.PutAsJsonAsync($"/admin/endpoints/{created["id"]}", legacy);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(2, (await updated.Content.ReadFromJsonAsync<JsonObject>())!["revision"]!.GetValue<int>());

        var stale = await host.Client.PutAsJsonAsync($"/admin/endpoints/{created["id"]}", legacy);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("expected revision 1, current revision 2", await stale.Content.ReadAsStringAsync());
    }

    private static Task<HttpResponseMessage> Send(TestHost host, string path, string json, string? tenant = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant", tenant);
        }

        return host.Client.SendAsync(request);
    }

    private sealed class FeatureAccessFilter : IDynamicEndpointFilter
    {
        public ValueTask OnRequestAsync(DynamicEndpointRequestContext context)
        {
            var feature = context.Endpoint.Definition.ProcessorConfig?["feature"]?.GetValue<string>();
            if (feature is not null && context.HttpContext.Request.Headers["X-Tenant"] != "pro")
            {
                context.Result = Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: $"Feature '{feature}' is not available.");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApiErrorFormatFilter : IDynamicEndpointFilter
    {
        public ValueTask OnValidationFailedAsync(DynamicValidationFailedContext context)
        {
            var code = context.Reason == DynamicRequestRejection.Validation ? "VALIDATION_FAILED" : context.Reason.ToString().ToUpperInvariant();
            context.Result = Results.Json(new
            {
                apiVersion = "1.0",
                error = new
                {
                    code,
                    message = context.Detail ?? context.Title,
                    details = context.Errors.Select(e => new { field = e.Key, e.Code, e.Message }),
                },
            }, statusCode: context.StatusCode);
            return ValueTask.CompletedTask;
        }
    }
}
