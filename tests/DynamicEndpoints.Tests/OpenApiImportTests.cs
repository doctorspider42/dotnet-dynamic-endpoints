using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
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

    private const string Routed = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Routed", "version": "1" },
          "x-dynamic-endpoints-processor": "root",
          "paths": {
            "/a": {
              "x-dynamic-endpoints-processor": "path",
              "get": { "operationId": "own", "tags": ["Pets"], "x-dynamic-endpoints-processor": "own",
                       "x-dynamic-endpoints-processor-config": { "greeting": "Hi" }, "responses": { "200": { "description": "ok" } } },
              "post": { "operationId": "fromPath", "tags": ["Pets"], "responses": { "201": { "description": "ok" } } }
            },
            "/b": { "get": { "operationId": "fromTag", "tags": ["Misc", "Pets"], "responses": { "200": { "description": "ok" } } } },
            "/c": { "get": { "operationId": "fromRoot", "tags": ["Misc"], "x-dynamic-endpoints-processor-config": { "x": 1 },
                             "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    [Fact]
    public async Task Processors_come_from_extensions_tags_and_options_in_a_documented_order()
    {
        await using var host = await TestHost.StartAsync(options: o => o.DefaultProcessor = "echo");
        var importer = host.Services.GetRequiredService<IDynamicEndpointOpenApiImporter>();
        var byTag = new Dictionary<string, OpenApiProcessorMapping>
        {
            ["pets"] = new() { Processor = "tagged", ProcessorConfig = new JsonObject { ["tag"] = true } },
        };
        var options = new OpenApiImportOptions { Processor = "option", ProcessorsByTag = byTag };

        var result = importer.Convert(JsonNode.Parse(Routed)!, options);
        Assert.Equal(["Pets", "Misc"], result.Tags);
        Assert.Equal((OpenApiProcessorSource.OperationExtension, "own"), Source(result, "own"));
        Assert.Equal("Hi", ById(result, "own").Definition!.ProcessorConfig!["greeting"]!.GetValue<string>());
        Assert.Equal((OpenApiProcessorSource.PathExtension, "path"), Source(result, "fromPath"));
        Assert.Equal((OpenApiProcessorSource.Tag, "tagged"), Source(result, "fromTag"));     // the first tag with a mapping
        Assert.Equal("tag 'pets'", ById(result, "fromTag").ProcessorReason);
        Assert.True(ById(result, "fromTag").Definition!.ProcessorConfig!["tag"]!.GetValue<bool>());
        Assert.Equal((OpenApiProcessorSource.DocumentExtension, "root"), Source(result, "fromRoot"));
        Assert.Contains(ById(result, "fromRoot").Unmapped, n => n.Contains("without 'x-dynamic-endpoints-processor'"));

        var withoutRoot = JsonNode.Parse(Routed)!.AsObject();
        withoutRoot.Remove("x-dynamic-endpoints-processor");
        result = importer.Convert(withoutRoot, options);
        Assert.Equal((OpenApiProcessorSource.Option, "option"), Source(result, "fromRoot"));
        result = importer.Convert(withoutRoot, new OpenApiImportOptions());
        Assert.Equal((OpenApiProcessorSource.Default, "echo"), Source(result, "fromRoot"));
        Assert.Null(ById(result, "fromRoot").Definition!.Processor);

        // Mock mode replaces the import options (tags, processor, default), not the extensions.
        result = importer.Convert(JsonNode.Parse(Routed)!, options with { Mock = true });
        Assert.Equal((OpenApiProcessorSource.OperationExtension, "own"), Source(result, "own"));
        Assert.Equal((OpenApiProcessorSource.PathExtension, "path"), Source(result, "fromPath"));
        Assert.Equal((OpenApiProcessorSource.DocumentExtension, "root"), Source(result, "fromTag"));
        result = importer.Convert(withoutRoot, options with { Mock = true });
        Assert.Equal((OpenApiProcessorSource.Mock, "response"), Source(result, "fromTag"));
        Assert.Equal(200, ById(result, "fromTag").Definition!.ProcessorConfig!["statusCode"]!.GetValue<int>());
    }

    [Fact]
    public async Task Tag_mappings_are_read_from_the_query_and_from_an_options_body()
    {
        await using var host = await TestHost.StartAsync();
        var document = JsonNode.Parse(Routed)!.AsObject();
        document.Remove("x-dynamic-endpoints-processor");

        var query = await (await Post(host, "/admin/endpoints/import/openapi?dryRun=true&processor=echo&processorByTag=Misc:greeting", document.ToJsonString(), "application/json"))
            .Content.ReadFromJsonAsync<OpenApiImportResult>(DynamicEndpointsJson.SerializerOptions);
        Assert.Equal((OpenApiProcessorSource.Tag, "greeting"), Source(query!, "fromTag"));
        Assert.Contains("processorConfig", ById(query!, "fromTag").Errors.Keys);   // greeting needs a configuration

        var envelope = new JsonObject
        {
            ["document"] = document.DeepClone(),
            ["options"] = JsonNode.Parse("""{ "processor": "echo", "processorsByTag": { "Misc": { "processor": "greeting", "processorConfig": { "greeting": "Hi" } } } }"""),
        };
        var body = await Post(host, "/admin/endpoints/import/openapi?dryRun=true", envelope.ToJsonString(), "application/json");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, body.StatusCode);   // the extensions name processors that don't exist
        var result = await body.Content.ReadFromJsonAsync<OpenApiImportResult>(DynamicEndpointsJson.SerializerOptions);
        Assert.Equal((OpenApiProcessorSource.Tag, "greeting"), Source(result!, "fromTag"));
        Assert.Empty(ById(result!, "fromTag").Errors);

        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, "/admin/endpoints/import/openapi?processorByTag=nope", Routed, "application/json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(host, "/admin/endpoints/import/openapi?mode=merge", Routed, "application/json")).StatusCode);
    }

    private const string Petstore = """
        {
          "openapi": "3.1.0",
          "info": { "title": "Petstore", "version": "1" },
          "paths": {
            "/pets": {
              "get": { "operationId": "listPets", "responses": { "200": { "description": "ok", "content": { "application/json": {
                "schema": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } },
                "examples": { "two": { "value": [{ "id": 1, "name": "Rex" }, { "id": 2, "name": "Tom" }] } } } } } } },
              "post": { "operationId": "createPet",
                "requestBody": { "content": { "application/json": { "schema": { "$ref": "#/components/schemas/NewPet" } } } },
                "responses": { "400": { "description": "bad" }, "201": { "description": "created", "content": { "application/json": {
                  "example": { "id": 3, "name": "Kitty" } } } } } }
            },
            "/pets/{petId}": {
              "parameters": [{ "name": "petId", "in": "path", "required": true, "schema": { "type": "integer" } }],
              "get": { "operationId": "getPet", "responses": {
                "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Pet" } } } },
                "404": { "description": "missing" } } },
              "delete": { "operationId": "deletePet", "responses": { "204": { "description": "deleted" } } }
            },
            "/stats/count": { "get": { "operationId": "countPets", "responses": { "200": { "description": "ok",
              "content": { "text/plain": { "schema": { "type": "string" }, "example": "2" } } } } } },
            "/legacy": { "get": { "operationId": "legacy", "responses": { "410": { "description": "gone",
              "content": { "application/problem+json": { "example": { "title": "Gone" } } } } } } },
            "/ping": { "get": { "operationId": "ping", "responses": { "default": { "description": "whatever" } } } }
          },
          "components": { "schemas": {
            "NewPet": { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } },
            "Pet": { "allOf": [
              { "type": "object", "properties": { "id": { "type": "integer", "minimum": 1 } } },
              { "$ref": "#/components/schemas/NewPet" },
              { "type": "object", "properties": { "tag": { "type": "string", "enum": ["dog", "cat"] } } } ] }
          } }
        }
        """;

    [Fact]
    public async Task Mock_mode_answers_with_the_documented_examples_and_status_codes()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddResponseTemplateProcessor());

        var import = await Post(host, "/admin/endpoints/import/openapi?mock=true&enabled=true", Petstore, "application/json");
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        var result = await import.Content.ReadFromJsonAsync<OpenApiImportResult>(DynamicEndpointsJson.SerializerOptions);
        Assert.Equal(7, result!.Created);
        Assert.All(result.Operations, o => Assert.Equal((OpenApiProcessorSource.Mock, "response"), (o.ProcessorSource, o.Processor)));
        Assert.Equal("mock: 200 with a body generated from the schema", ById(result, "getPet").ProcessorReason);

        var list = await host.Client.GetAsync("/pets");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("""[{"id":1,"name":"Rex"},{"id":2,"name":"Tom"}]""", await list.Content.ReadAsStringAsync());

        var created = await host.Client.PostAsJsonAsync("/pets", new { name = "Kitty" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);   // the 2xx response, not the first one
        Assert.Equal("""{"id":3,"name":"Kitty"}""", await created.Content.ReadAsStringAsync());

        var generated = await host.Client.GetAsync("/pets/7");
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Equal("""{"id":1,"name":"string","tag":"dog"}""", await generated.Content.ReadAsStringAsync());   // allOf merged

        var deleted = await host.Client.DeleteAsync("/pets/7");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsStringAsync());

        var count = await host.Client.GetAsync("/stats/count");
        Assert.Equal(("2", "text/plain"), (await count.Content.ReadAsStringAsync(), count.Content.Headers.ContentType!.MediaType));

        var legacy = await host.Client.GetAsync("/legacy");     // no 2xx: the documented status
        Assert.Equal((HttpStatusCode.Gone, "application/problem+json"), (legacy.StatusCode, legacy.Content.Headers.ContentType!.MediaType));
        Assert.Equal("""{"title":"Gone"}""", await legacy.Content.ReadAsStringAsync());
        Assert.Contains(ById(result, "legacy").Unmapped, n => n.Contains("no 2xx response"));

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.GetAsync("/ping")).StatusCode);

        // An extension still wins over mock mode.
        var document = JsonNode.Parse(Petstore)!;
        document["paths"]!["/ping"]!["get"]!["x-dynamic-endpoints-processor"] = "echo";
        var converted = host.Services.GetRequiredService<IDynamicEndpointOpenApiImporter>().Convert(document, new OpenApiImportOptions { Mock = true });
        Assert.Equal((OpenApiProcessorSource.OperationExtension, "echo"), Source(converted, "ping"));
    }

    [Fact]
    public async Task Mock_mode_without_the_response_processor_says_how_to_register_it()
    {
        await using var host = await TestHost.StartAsync();

        var response = await Post(host, "/admin/endpoints/import/openapi?mock=true", Petstore, "application/json");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<OpenApiImportResult>(DynamicEndpointsJson.SerializerOptions);
        Assert.All(result!.Operations, o => Assert.Contains("AddResponseTemplateProcessor()", o.Errors["processor"][0]));
        Assert.Empty(await host.Manager.ListAsync());
    }

    private const string ShopV1 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Shop", "version": "1" },
          "paths": {
            "/orders/{id}": {
              "get": { "operationId": "getOrder", "summary": "Get order",
                "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }],
                "responses": { "200": { "description": "ok" } } },
              "delete": { "operationId": "deleteOrder",
                "parameters": [{ "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }],
                "responses": { "204": { "description": "deleted" } } }
            },
            "/orders": { "get": { "operationId": "listOrders", "summary": "List orders",
              "parameters": [{ "name": "page", "in": "query", "schema": { "type": "integer" } }],
              "responses": { "200": { "description": "ok" } } } },
            "/customers": { "get": { "operationId": "listCustomers", "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

    // getOrder renamed and with a new parameter, listCustomers gone, a route that is taken by a hand-made endpoint, a new operation.
    private static string ShopV2()
    {
        var document = JsonNode.Parse(ShopV1)!;
        var paths = document["paths"]!.AsObject();
        var get = paths["/orders/{id}"]!["get"]!;
        get["operationId"] = "fetchOrder";
        get["parameters"]!.AsArray().Add(JsonNode.Parse("""{ "name": "expand", "in": "query", "schema": { "type": "boolean" } }"""));
        paths.Remove("/customers");
        paths["/reports"] = JsonNode.Parse("""{ "get": { "operationId": "reports", "responses": { "200": { "description": "ok" } } } }""");
        paths["/orders"]!["post"] = JsonNode.Parse("""{ "operationId": "createOrder", "responses": { "201": { "description": "ok" } } }""");
        return document.ToJsonString();
    }

    [Fact]
    public async Task Re_imports_update_and_sync_imported_endpoints_and_keep_admin_changes()
    {
        await using var host = await TestHost.StartAsync(options: o => o.DefaultProcessor = "echo");
        var reports = await host.Manager.CreateAsync(DynamicEndpoint.Get("/reports").HandledBy("echo"));
        var other = await host.Manager.CreateAsync(DynamicEndpoint.Get("/other").HandledBy("echo"));

        var first = await Import(host, "", ShopV1);
        Assert.Equal(4, first.Created);
        var getOrder = await Find(host, "GET", "/orders/{id}");
        Assert.Equal(new DynamicEndpointOrigin { Kind = "openapi", Document = "Shop", Operation = "getOrder" }, getOrder.Origin);
        Assert.Equal((OpenApiProcessorSource.Default, "echo"), Source(first, "getOrder"));

        // What admins changed after the import.
        await host.Manager.SetEnabledAsync(getOrder.Id, true);
        var listOrders = await Find(host, "GET", "/orders");
        await host.Manager.UpdateAsync(listOrders with { Processor = "greeting", ProcessorConfig = new JsonObject { ["greeting"] = "Hi" }, AuthorizationPolicy = "admins" });

        // create (the default) still skips what was imported before.
        var again = await Import(host, "dryRun=true", ShopV2());
        Assert.Equal(DynamicEndpointImportAction.Skip, ById(again, "fetchOrder").Action);
        Assert.Equal(DynamicEndpointImportAction.Skip, ById(again, "reports").Action);
        Assert.Equal(1, again.Created);

        var upsert = await Import(host, "mode=upsert&dryRun=true", ShopV2());
        Assert.True(upsert.Succeeded);
        var fetch = ById(upsert, "fetchOrder");
        Assert.Equal((DynamicEndpointImportAction.Update, getOrder.Id), (fetch.Action, fetch.Id));    // found by method and route
        Assert.Equal(["origin", "parameters"], fetch.Changes.Order());
        Assert.Equal(DynamicEndpointImportAction.Unchanged, ById(upsert, "listOrders").Action);
        Assert.Equal((OpenApiProcessorSource.Kept, "greeting"), Source(upsert, "listOrders"));
        Assert.Equal(DynamicEndpointImportAction.Unchanged, ById(upsert, "deleteOrder").Action);
        Assert.Equal(DynamicEndpointImportAction.Create, ById(upsert, "createOrder").Action);
        var skipped = ById(upsert, "reports");
        Assert.Equal((DynamicEndpointImportAction.Skip, reports.Id), (skipped.Action, skipped.Id));
        Assert.Contains("wasn't imported from this document", skipped.Reason);
        Assert.DoesNotContain(upsert.Operations, o => o.Action == DynamicEndpointImportAction.Delete);
        Assert.Equal(6, (await host.Manager.ListAsync()).Count);   // a dry run writes nothing

        var sync = await Import(host, "mode=sync", ShopV2());
        Assert.True(sync.Succeeded);
        var deleted = Assert.Single(sync.Operations, o => o.Action == DynamicEndpointImportAction.Delete);
        Assert.Equal(("GET", "/customers", "listCustomers"), (deleted.Method, deleted.Path, deleted.OperationId));
        Assert.Equal((1, 1, 1, 2), (sync.Created, sync.Updated, sync.Deleted, sync.Unchanged));

        var updated = await Find(host, "GET", "/orders/{id}");
        Assert.Equal(getOrder.Id, updated.Id);
        Assert.True(updated.Enabled);                                      // kept
        Assert.Equal("fetchOrder", updated.Origin!.Operation);
        Assert.Contains(updated.Parameters, p => p.Name == "expand");
        Assert.Equal("""{"id":5,"expand":true}""", await host.Client.GetStringAsync("/orders/5?expand=true"));
        var kept = await Find(host, "GET", "/orders");
        Assert.Equal(("greeting", "admins"), (kept.Processor, kept.AuthorizationPolicy));
        Assert.Null(await FindOrNull(host, "GET", "/customers"));
        Assert.False((await Find(host, "POST", "/orders")).Enabled);
        Assert.Equal(reports.Revision, (await Find(host, "GET", "/reports")).Revision);   // hand-made: untouched
        Assert.Equal(other.Revision, (await Find(host, "GET", "/other")).Revision);

        // Once the import chooses a processor, the document owns it.
        var withProcessor = await Import(host, "mode=upsert&dryRun=true&processor=echo", ShopV2());
        var owned = ById(withProcessor, "listOrders");
        Assert.Equal(DynamicEndpointImportAction.Update, owned.Action);
        Assert.Equal(["processor", "processorConfig"], owned.Changes.Order());
        Assert.Equal(0, (await Import(host, "mode=sync&dryRun=true", ShopV2())).Deleted);
    }

    [Fact]
    public async Task A_tenant_sync_deletes_only_the_tenants_own_imported_endpoints()
    {
        await using var host = await TestHost.StartAsync(
            options: o => o.DefaultProcessor = "echo",
            configure: b => b.UseMultiTenancy(t => t.FromHeader()),
            configureApp: app => app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints"));
        Assert.Equal(4, (await Import(host, "", ShopV1)).Created);   // shared
        Assert.Equal(4, (await Import(host, "", ShopV1, "/admin/tenants/acme/endpoints")).Created);
        Assert.Equal(4, (await Import(host, "", ShopV1, "/admin/tenants/globex/endpoints")).Created);

        var document = JsonNode.Parse(ShopV1)!;
        document["paths"]!.AsObject().Remove("/customers");
        var sync = await Import(host, "mode=sync", document.ToJsonString(), "/admin/tenants/acme/endpoints");

        Assert.Equal((1, 3), (sync.Deleted, sync.Unchanged));
        var customers = (await host.Manager.ListAsync()).Where(s => s.Definition.Route == "/customers").Select(s => s.Definition.Tenant).ToList();
        Assert.Equal([null, "globex"], customers.Order());
        Assert.All((await host.Manager.ListAsync()).Where(s => s.Definition.Tenant == "acme"), s => Assert.NotEqual("/customers", s.Definition.Route));
    }

    private static async Task<OpenApiImportResult> Import(TestHost host, string query, string document, string api = "/admin/endpoints")
    {
        var response = await Post(host, $"{api}/import/openapi?{query}", document, "application/json");
        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.UnprocessableEntity, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<OpenApiImportResult>(DynamicEndpointsJson.SerializerOptions))!;
    }

    private static async Task<DynamicEndpointDefinition?> FindOrNull(TestHost host, string method, string route) =>
        (await host.Manager.ListAsync()).Select(s => s.Definition).SingleOrDefault(d => d.Method == method && d.Route == route && d.Tenant is null);

    private static async Task<DynamicEndpointDefinition> Find(TestHost host, string method, string route) =>
        await FindOrNull(host, method, route) ?? throw new InvalidOperationException($"{method} {route} not found.");

    private static OpenApiImportedOperation ById(OpenApiImportResult result, string operationId) =>
        result.Operations.Single(o => o.OperationId == operationId);

    private static (OpenApiProcessorSource, string?) Source(OpenApiImportResult result, string operationId) =>
        ById(result, operationId) is var o ? (o.ProcessorSource, o.Processor) : default;

    private static OpenApiImportedOperation Operation(OpenApiImportResult result, string method, string path) =>
        result.Operations.Single(o => o.Method == method && o.Path == path);

    private static Task<HttpResponseMessage> Post(TestHost host, string url, string body, string contentType) =>
        host.Client.PostAsync(url, new StringContent(body, Encoding.UTF8, contentType));
}
