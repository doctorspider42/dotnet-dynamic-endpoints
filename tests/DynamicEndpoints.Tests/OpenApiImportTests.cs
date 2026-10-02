using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class OpenApiImportTests
{
    private const string Document = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Shop", "version": "1" },
          "security": [{ "ApiKey": [] }],
          "paths": {
            "/orders/{orderId}": {
              "parameters": [{ "name": "orderId", "in": "path", "required": true, "schema": { "type": "integer", "minimum": 1 } }],
              "get": {
                "operationId": "getOrder",
                "summary": "Get order",
                "tags": ["Orders"],
                "parameters": [
                  { "name": "expand", "in": "query", "schema": { "type": "string", "enum": ["lines", "customer"] } },
                  { "name": "X-Request-Id", "in": "header", "schema": { "type": "string", "format": "uuid" } },
                  { "name": "session", "in": "cookie", "schema": { "type": "string" } }
                ],
                "responses": {
                  "200": { "description": "ok", "content": { "application/json": {
                    "schema": { "$ref": "#/components/schemas/Order" },
                    "example": { "id": 1, "status": "new" } } } },
                  "404": { "description": "missing" }
                }
              },
              "head": { "responses": { "200": { "description": "ok" } } }
            },
            "/orders": {
              "post": {
                "summary": "Create order",
                "tags": ["Orders"],
                "requestBody": { "$ref": "#/components/requestBodies/NewOrder" },
                "responses": { "201": { "description": "created" } }
              }
            },
            "/documents": {
              "post": {
                "security": [],
                "requestBody": { "content": { "multipart/form-data": {
                  "schema": { "type": "object", "required": ["file"], "properties": {
                    "title": { "type": "string", "maxLength": 100 },
                    "file": { "type": "string", "format": "binary" } } },
                  "encoding": { "file": { "contentType": "application/pdf, image/png" } } } } },
                "responses": { "200": { "description": "ok" } }
              }
            }
          },
          "components": {
            "requestBodies": {
              "NewOrder": { "required": true, "content": { "application/json": { "schema": {
                "allOf": [
                  { "$ref": "#/components/schemas/OrderBase" },
                  { "type": "object", "required": ["email"], "properties": {
                    "email": { "type": "string", "format": "email" },
                    "quantity": { "type": "integer", "exclusiveMinimum": true, "minimum": 0, "maximum": 100, "default": 1 },
                    "payment": { "oneOf": [{ "type": "string" }, { "type": "integer" }] } } }
                ] } } } }
            },
            "schemas": {
              "OrderBase": { "type": "object", "required": ["sku"], "properties": {
                "sku": { "type": "string", "pattern": "^[A-Z]{3}-[0-9]+$", "example": "ABC-1" },
                "address": { "type": "object", "nullable": true, "properties": {
                  "city": { "type": "string", "minLength": 1 }, "zip": { "type": "string", "pattern": "^[0-9]{5}$" } } },
                "tags": { "type": "array", "maxItems": 5, "items": { "type": "string", "maxLength": 10 } } } },
              "Order": { "type": "object", "properties": { "id": { "type": "integer" }, "base": { "$ref": "#/components/schemas/OrderBase" } } }
            }
          }
        }
        """;

    [Fact]
    public async Task Operations_map_to_definition_skeletons_with_types_constraints_and_notes()
    {
        await using var host = await TestHost.StartAsync();
        var importer = host.Services.GetRequiredService<IDynamicEndpointOpenApiImporter>();

        var result = importer.Convert(JsonNode.Parse(Document)!, new OpenApiImportOptions { Processor = "echo", RoutePrefix = "/shop" });

        var get = Operation(result, "GET", "/orders/{orderId}").Definition!;
        Assert.Equal("/shop/orders/{orderId}", get.Route);
        Assert.Equal("Get order", get.Name);
        Assert.Equal("Orders", get.Group);
        Assert.True(get.RequireAuthorization);
        Assert.False(get.Enabled);
        var orderId = get.Parameters.Single(p => p.Name == "orderId");
        Assert.Equal((ParameterSource.Route, ParameterType.Integer, 1m, true), (orderId.Source, orderId.Type, orderId.Minimum, orderId.Required));
        Assert.Equal(["lines", "customer"], get.Parameters.Single(p => p.Name == "expand").AllowedValues!.Select(v => v!.GetValue<string>()));
        var requestId = get.Parameters.Single(p => p.Source == ParameterSource.Header);
        Assert.Equal(("xRequestId", "X-Request-Id", ParameterType.Guid), (requestId.Name, requestId.SourceName, requestId.Type));
        Assert.Equal("integer", get.ResponseSchema!["properties"]!["id"]!["type"]!.GetValue<string>());
        Assert.NotNull(get.ResponseSchema["properties"]!["base"]!["properties"]!["sku"]);     // references are inlined
        Assert.Equal("new", get.ResponseExample!["status"]!.GetValue<string>());
        var getNotes = Operation(result, "GET", "/orders/{orderId}").Unmapped;
        Assert.Contains(getNotes, n => n.Contains("cookie parameter 'session'"));
        Assert.Contains(getNotes, n => n.Contains("security (ApiKey)"));

        var head = Operation(result, "HEAD", "/orders/{orderId}");
        Assert.Null(head.Definition);
        Assert.Equal(DynamicEndpointImportAction.Skip, head.Action);

        var post = Operation(result, "POST", "/orders");
        var body = post.Definition!.Parameters.ToDictionary(p => p.Name);
        Assert.All(body.Values, p => Assert.Equal(ParameterSource.Body, p.Source));
        Assert.Equal(("^[A-Z]{3}-[0-9]+$", true, "ABC-1"), (body["sku"].Pattern, body["sku"].Required, body["sku"].Example!.GetValue<string>()));
        Assert.Equal((ParameterFormat.Email, true), (body["email"].Format, body["email"].Required));
        Assert.Equal((1m, 100m, 1), (body["quantity"].Minimum, body["quantity"].Maximum, body["quantity"].Default!.GetValue<int>()));
        Assert.Equal((ParameterType.Array, ParameterType.String, 5, 10), (body["tags"].Type, body["tags"].ItemType, body["tags"].MaxItems, body["tags"].MaxLength));
        var address = body["address"];
        Assert.Equal(ParameterType.Object, address.Type);
        Assert.Equal("""["object","null"]""", address.Schema!["type"]!.ToJsonString());
        Assert.Null(address.Schema["properties"]!["zip"]!["pattern"]);
        Assert.Contains(post.Unmapped, n => n.Contains("'pattern' inside an object schema"));
        Assert.Contains(post.Unmapped, n => n.Contains("body property 'payment': composed schema"));

        var documents = Operation(result, "POST", "/documents").Definition!;
        Assert.True(documents.AllowAnonymous);
        var file = documents.Parameters.Single(p => p.Name == "file");
        Assert.Equal((ParameterSource.Form, ParameterType.File, true), (file.Source, file.Type, file.Required));
        Assert.Equal(["application/pdf", "image/png"], file.AllowedContentTypes);
        Assert.Equal(100, documents.Parameters.Single(p => p.Name == "title").MaxLength);
    }

    [Fact]
    public async Task Admin_api_dry_runs_creates_and_skips_existing_routes()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddYamlFormat());

        var dryRun = await Post(host, "/admin/endpoints/import/openapi?dryRun=true&processor=echo", Document, "application/json");
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        var preview = await dryRun.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, preview!["created"]!.GetValue<int>());
        Assert.Empty(await host.Manager.ListAsync());

        // The same document as YAML.
        var yaml = Yaml.DynamicEndpointsYaml.Write(JsonNode.Parse(Document));
        var created = await Post(host, "/admin/endpoints/import/openapi?processor=echo&tag=Orders", yaml, "application/yaml");
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(2, (await created.Content.ReadFromJsonAsync<JsonObject>())!["created"]!.GetValue<int>());
        var stored = await host.Manager.ListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, s => Assert.Equal(DynamicEndpointStatus.Disabled, s.Status));

        var again = await (await Post(host, "/admin/endpoints/import/openapi?processor=echo", Document, "application/json")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal((1, 3), (again!["created"]!.GetValue<int>(), again["skipped"]!.GetValue<int>()));   // 2 existing + HEAD
    }

    [Fact]
    public async Task Invalid_skeletons_and_documents_are_reported()
    {
        await using var host = await TestHost.StartAsync();

        var noProcessor = await Post(host, "/admin/endpoints/import/openapi", Document, "application/json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, noProcessor.StatusCode);
        var result = await noProcessor.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, result!["invalid"]!.GetValue<int>());
        Assert.Contains("processor", result["operations"]![0]!["errors"]!.AsObject().Select(e => e.Key));
        Assert.Empty(await host.Manager.ListAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, "/admin/endpoints/import/openapi", """{ "swagger": "2.0" }""", "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, "/admin/endpoints/import/openapi", "{ nope", "application/json")).StatusCode);
    }

    private static OpenApiImportedOperation Operation(OpenApiImportResult result, string method, string path) =>
        result.Operations.Single(o => o.Method == method && o.Path == path);

    private static Task<HttpResponseMessage> Post(TestHost host, string url, string body, string contentType) =>
        host.Client.PostAsync(url, new StringContent(body, Encoding.UTF8, contentType));
}
