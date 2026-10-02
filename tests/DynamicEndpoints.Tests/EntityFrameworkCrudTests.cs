using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.EntityFrameworkCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class EntityFrameworkCrudTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"dynamic-endpoints-crud-{Guid.NewGuid():N}.db");

    private string ConnectionString => $"Data Source={_databasePath};Pooling=False";

    private static void DefaultCrud(DynamicCrudBuilder<CrudDbContext> crud) => crud
        .Entity<CrudProduct>(e => e
            .Fields(p => p.Id, p => p.Sku, p => p.Name, p => p.Price, p => p.Stock, p => p.Status)
            .ReadOnly(p => p.CreatedAt)
            .Filterable(p => p.Sku, p => p.Name, p => p.Price, p => p.Status)
            .Sortable(p => p.Name, p => p.Price)
            .TenantColumn(p => p.TenantId))
        .Entity<CrudOrder>("orders", e => e
            .AllFields(except: o => o.InternalNote)
            .Operations(CrudOperations.All & ~CrudOperations.Delete))
        .Entity<CrudCountry>(e => e.AllFields().SharedAcrossTenants().Operations(CrudOperations.Read | CrudOperations.Create));

    private Task<TestHost> StartAsync(Action<DynamicCrudBuilder<CrudDbContext>>? crud = null, Action<IServiceCollection>? services = null) =>
        TestHost.StartAsync(
            configure: b =>
            {
                b.Services.AddDbContext<CrudDbContext>(o => o.UseSqlite(ConnectionString));
                services?.Invoke(b.Services);
                b.UseMultiTenancy(t => t.FromHeader()).AddEntityFrameworkCrud(crud ?? DefaultCrud);
            },
            configureApp: app =>
            {
                using (var scope = app.Services.CreateScope())
                {
                    scope.ServiceProvider.GetRequiredService<CrudDbContext>().Database.EnsureCreated();
                }

                app.MapDynamicEndpointsTenantAdmin("/admin/tenants/{tenant}/endpoints");
            });

    private static async Task SeedAsync(TestHost host, params object[] entities)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CrudDbContext>();
        db.AddRange(entities);
        await db.SaveChangesAsync();
    }

    private static async Task<T> QueryAsync<T>(TestHost host, Func<CrudDbContext, Task<T>> query)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<CrudDbContext>());
    }

    private static Task<HttpResponseMessage> SendAsync(TestHost host, HttpMethod method, string url, string? tenant = "acme", object? body = null, string? ifMatch = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (tenant is not null)
        {
            request.Headers.Add("X-Tenant-Id", tenant);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return host.Client.SendAsync(request);
    }

    private static CrudProduct Product(string sku, string name, decimal price, string tenant = "acme", int stock = 0) =>
        new() { Sku = sku, Name = name, Price = price, Stock = stock, TenantId = tenant, Version = Guid.NewGuid(), InternalNote = "secret" };

    private static async Task<string[]> ConfigErrorsAsync(TestHost host, DynamicEndpointDefinition definition) =>
        (await host.Manager.ValidateAsync(definition)).Errors.GetValueOrDefault("processorConfig") ?? [];

    [Fact]
    public async Task Entity_names_come_from_the_attribute_the_DbSet_property_or_the_registration()
    {
        await using var host = await StartAsync();

        var entities = (await host.Client.GetFromJsonAsync<JsonArray>("/admin/endpoints/crud/entities"))!;
        Assert.Equal(["countries", "orders", "products"], entities.Select(e => (string)e!["name"]!));

        var products = entities.Single(e => (string)e!["name"]! == "products")!;
        Assert.Equal(["list", "get", "create", "update", "patch", "delete"], products["operations"]!.AsArray().Select(o => (string)o!));
        Assert.Equal("id", (string?)products["key"]);
        Assert.True((bool)products["tenantColumn"]!);
        Assert.True((bool)products["concurrencyToken"]!);

        // Seeders need no magic strings.
        var list = DynamicEndpoint.Get("/p").HandledByCrud<CrudProduct>(CrudOperation.List, c => c.PageSize = 20).Build();
        Assert.Equal("ef-crud", list.Processor);
        Assert.Equal("products", (string?)list.ProcessorConfig!["entity"]);
        Assert.Equal("list", (string?)list.ProcessorConfig["operation"]);
        Assert.Equal(20, (int)list.ProcessorConfig["pageSize"]!);
        Assert.Equal("orders", (string?)DynamicEndpoint.Get("/o").HandledByCrud<CrudOrder>(CrudOperation.List).Build().ProcessorConfig!["entity"]);
        Assert.Equal("countries", (string?)DynamicEndpoint.Get("/c").HandledByCrud<CrudCountry>(CrudOperation.List).Build().ProcessorConfig!["entity"]);
        Assert.Throws<InvalidOperationException>(() => DynamicEndpoint.Get("/x").HandledByCrud<CrudCategory>(CrudOperation.List));
    }

    [Fact]
    public async Task Unlisted_fields_are_never_returned_nor_written()
    {
        await using var host = await StartAsync();

        // Admins can't declare fields the developer didn't expose, nor read-only ones.
        var hidden = await ConfigErrorsAsync(host, DynamicEndpoint.Post("/products").HandledByCrud<CrudProduct>(CrudOperation.Create)
            .FromBody("sku").FromBody("internalNote").FromBody("tenantId"));
        Assert.Contains(hidden, e => e.Contains("'internalNote' is not a field"));
        Assert.Contains(hidden, e => e.Contains("'tenantId' is not a field"));
        var readOnly = await ConfigErrorsAsync(host, DynamicEndpoint.Post("/products").HandledByCrud<CrudProduct>(CrudOperation.Create).FromBody("createdAt").FromBody("id"));
        Assert.Contains(readOnly, e => e.Contains("'createdAt' of 'products' is read-only"));
        Assert.Contains(readOnly, e => e.Contains("'id' of 'products' is read-only"));

        await host.Manager.CreateAsync(DynamicEndpoint.Post("/products").HandledByCrud<CrudProduct>(CrudOperation.Create)
            .FromBody("sku", p => p.String().Required()).FromBody("name", p => p.String()).FromBody("price", p => p.Number()));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Get).FromRoute("id", p => p.Integer()));

        // Undeclared body fields are ignored – they never reach the processor.
        var created = await SendAsync(host, HttpMethod.Post, "/products", body: new
        {
            sku = "A-1", name = "Anvil", price = 12.5, id = 999, tenantId = "globex", internalNote = "pwned", createdAt = "2001-01-01T00:00:00Z", stock = 7,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["id", "sku", "name", "price", "stock", "status", "createdAt"], body.Select(p => p.Key));
        Assert.Equal("Draft", (string?)body["status"]);

        var row = await QueryAsync(host, db => db.Products.SingleAsync());
        Assert.NotEqual(999, row.Id);
        Assert.Equal("acme", row.TenantId);
        Assert.Null(row.InternalNote);
        Assert.Equal(0, row.Stock);
        Assert.Equal(default, row.CreatedAt);
        Assert.Equal($"/products/{row.Id}", created.Headers.Location!.OriginalString);

        var fetched = (await (await SendAsync(host, HttpMethod.Get, $"/products/{row.Id}")).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["id", "sku", "name", "price", "stock", "status", "createdAt"], fetched.Select(p => p.Key));
        Assert.Equal(12.5m, (decimal)fetched["price"]!);
    }

    [Fact]
    public async Task AllFields_skips_excepted_fields_and_navigations_and_protects_keys_generated_values_and_shadow_properties()
    {
        await using var host = await StartAsync();
        var orders = (await host.Client.GetFromJsonAsync<JsonArray>("/admin/endpoints/crud/entities"))!.Single(e => (string)e!["name"]! == "orders")!;
        var fields = orders["fields"]!.AsArray().ToDictionary(f => (string)f!["name"]!, f => f!.AsObject());

        Assert.Equal(["id", "customer", "total", "orderedOn", "lastModified"], fields.Keys);
        Assert.True((bool)fields["id"]["readOnly"]!);
        Assert.False((bool)fields["id"]["creatable"]!);
        Assert.True((bool)fields["lastModified"]["readOnly"]!);
        Assert.False((bool)fields["customer"]["readOnly"]!);
        Assert.True((bool)fields["customer"]["required"]!);
        Assert.Equal(50, (int)fields["customer"]["schema"]!["maxLength"]!);
        Assert.Equal(99999999.99m, (decimal)fields["total"]["schema"]!["maximum"]!);
        Assert.Equal("date", (string?)fields["orderedOn"]["schema"]!["format"]);

        var errors = await ConfigErrorsAsync(host, DynamicEndpoint.Patch("/orders/{id}").HandledByCrud<CrudOrder>(CrudOperation.Patch)
            .FromRoute("id", p => p.Guid()).FromBody("id").FromBody("lastModified").FromBody("lines").FromBody("internalNote"));
        Assert.Contains(errors, e => e.Contains("'id' of 'orders' is read-only"));
        Assert.Contains(errors, e => e.Contains("'lastModified' of 'orders' is read-only"));
        Assert.Contains(errors, e => e.Contains("'lines' is not a field"));
        Assert.Contains(errors, e => e.Contains("'internalNote' is not a field"));

        // A key that isn't generated can be set on create, but never changed.
        var countries = await ConfigErrorsAsync(host, DynamicEndpoint.Post("/countries").HandledByCrud<CrudCountry>(CrudOperation.Create).FromBody("code").FromBody("name"));
        Assert.Empty(countries);
        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Delete("/orders/{id}").HandledByCrud<CrudOrder>(CrudOperation.Delete).FromRoute("id", p => p.Guid())),
            e => e.Contains("Operation 'delete' is not allowed for 'orders'"));
    }

    [Fact]
    public async Task Definitions_are_checked_against_the_allowlist()
    {
        await using var host = await StartAsync();

        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/x").HandledBy("ef-crud", new { entity = "categories" })),
            e => e.Contains("Unknown entity 'categories'. Available: countries, orders, products."));
        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/x").HandledBy("ef-crud", new { entity = "products", operation = "delete" })),
            e => e.Contains("needs a DELETE endpoint"));
        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/x").HandledByCrud<CrudProduct>(CrudOperation.Get)),
            e => e.Contains("route parameter 'id'"));
        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/x").HandledBy("ef-crud", new { entity = "products", typo = 1 })),
            e => e.Contains("typo"));

        var list = DynamicEndpoint.Get("/x").FromQuery("q");
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledByCrud<CrudProduct>(CrudOperation.List, c => c.Filters = [new() { Field = "stock", Parameter = "q" }])),
            e => e.Contains("'stock' is not a filterable field. Filterable: sku, name, price, status."));
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledByCrud<CrudProduct>(CrudOperation.List, c => c.Filters = [new() { Field = "price", Operator = CrudFilterOperator.Contains, Parameter = "q" }])),
            e => e.Contains("'contains' can't be used on 'price'"));
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledByCrud<CrudProduct>(CrudOperation.List, c => c.Filters = [new() { Field = "name", Parameter = "missing" }])),
            e => e.Contains("'missing' is not a query, header or route parameter"));
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledByCrud<CrudProduct>(CrudOperation.List, c => c.Sort = "-stock")),
            e => e.Contains("Can't sort by 'stock'. Sortable: name, price."));
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledByCrud<CrudProduct>(CrudOperation.List, c => c.PageSize = 500)),
            e => e.Contains("'pageSize' can't be larger than 'maxPageSize'"));
        Assert.Contains(await ConfigErrorsAsync(host, list.HandledBy("ef-crud", new { entity = "products", filters = new[] { new { field = "name", @operator = "like", parameter = "q" } } })),
            e => e.Contains("Invalid configuration"));
    }

    [Fact]
    public async Task Lists_page_sort_and_filter_on_allowed_fields_only()
    {
        await using var host = await StartAsync();
        await SeedAsync(host,
            Product("A", "Anvil", 30), Product("B", "Bolt", 1), Product("C", "Crate", 20), Product("D", "Drill", 50), Product("E", "Elbow", 5),
            Product("G", "Gear", 99, tenant: "globex"));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/products").HandledByCrud<CrudProduct>(CrudOperation.List, c =>
            {
                c.PageSize = 2;
                c.MaxPageSize = 3;
                c.Sort = "name";
                c.SortParameter = "sort";
                c.Filters =
                [
                    new() { Field = "price", Operator = CrudFilterOperator.Gte, Parameter = "minPrice" },
                    new() { Field = "name", Operator = CrudFilterOperator.StartsWith, Parameter = "prefix" },
                    new() { Field = "sku", Operator = CrudFilterOperator.In, Parameter = "skus" },
                ];
            })
            .FromQuery("page", p => p.Integer()).FromQuery("pageSize", p => p.Integer()).FromQuery("sort")
            .FromQuery("minPrice", p => p.Number()).FromQuery("prefix").FromQuery("skus", p => p.ArrayOf(ParameterType.String)));

        async Task<JsonObject> ListAsync(string query) =>
            (await (await SendAsync(host, HttpMethod.Get, "/products" + query)).Content.ReadFromJsonAsync<JsonObject>())!;
        static string[] Names(JsonObject page) => page["items"]!.AsArray().Select(i => (string)i!["name"]!).ToArray();

        var first = await ListAsync("");
        Assert.Equal(["Anvil", "Bolt"], Names(first));
        Assert.Equal(5, (int)first["total"]!);
        Assert.Equal(1, (int)first["page"]!);
        Assert.Equal(2, (int)first["pageSize"]!);
        Assert.Equal(["Elbow"], Names(await ListAsync("?page=3")));
        Assert.Equal(["Drill", "Anvil", "Crate"], Names(await ListAsync("?sort=-price&pageSize=3")));
        Assert.Equal(["Anvil", "Drill"], Names(await ListAsync("?minPrice=25")));
        Assert.Equal(["Crate"], Names(await ListAsync("?prefix=Cr")));
        Assert.Equal(["Bolt", "Elbow"], Names(await ListAsync("?skus=E&skus=B&skus=G")));
        Assert.Equal(3, (int)(await ListAsync("?minPrice=1&prefix=x&pageSize=3"))["pageSize"]!);

        var tooLarge = await SendAsync(host, HttpMethod.Get, "/products?pageSize=4");
        Assert.Equal(HttpStatusCode.BadRequest, tooLarge.StatusCode);
        Assert.Contains("pageSize", await tooLarge.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(host, HttpMethod.Get, "/products?page=0")).StatusCode);

        var hiddenSort = await SendAsync(host, HttpMethod.Get, "/products?sort=stock");
        Assert.Equal(HttpStatusCode.BadRequest, hiddenSort.StatusCode);
        Assert.Contains("Can't sort by 'stock'", await hiddenSort.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ETags_guard_updates_and_deletes()
    {
        await using var host = await StartAsync();
        await SeedAsync(host, Product("A", "Anvil", 30));
        var id = await QueryAsync(host, db => db.Products.Select(p => p.Id).SingleAsync());
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Get).FromRoute("id", p => p.Integer()));
        await host.Manager.CreateAsync(DynamicEndpoint.Put("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Update)
            .FromRoute("id", p => p.Integer()).FromBody("sku", p => p.Required()).FromBody("name").FromBody("stock", p => p.Integer()));
        await host.Manager.CreateAsync(DynamicEndpoint.Patch("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Patch, c => c.RequireIfMatch = true)
            .FromRoute("id", p => p.Integer()).FromBody("name"));
        await host.Manager.CreateAsync(DynamicEndpoint.Delete("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Delete).FromRoute("id", p => p.Integer()));

        var get = await SendAsync(host, HttpMethod.Get, $"/products/{id}");
        var etag = get.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Get, $"/products/{id + 100}")).StatusCode);

        var stale = await SendAsync(host, HttpMethod.Put, $"/products/{id}", body: new { sku = "A", name = "x" }, ifMatch: "\"stale\"");
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal("application/problem+json", stale.Content.Headers.ContentType!.MediaType);

        // PUT replaces the declared fields: 'stock' wasn't sent, so it is reset.
        await QueryAsync(host, db => db.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 5)));
        var updated = await SendAsync(host, HttpMethod.Put, $"/products/{id}", body: new { sku = "A", name = "Anvil 2" }, ifMatch: etag);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var newTag = updated.Headers.ETag!.Tag;
        Assert.NotEqual(etag, newTag);
        Assert.Equal(0, (int)(await updated.Content.ReadFromJsonAsync<JsonObject>())!["stock"]!);
        Assert.Equal(newTag, (await SendAsync(host, HttpMethod.Get, $"/products/{id}")).Headers.ETag!.Tag);

        Assert.Equal((HttpStatusCode)428, (await SendAsync(host, HttpMethod.Patch, $"/products/{id}", body: new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(host, HttpMethod.Patch, $"/products/{id}", body: new { name = "x" }, ifMatch: etag)).StatusCode);
        var patched = await SendAsync(host, HttpMethod.Patch, $"/products/{id}", body: new { name = "Anvil 3" }, ifMatch: newTag);
        Assert.Equal("Anvil 3", (string?)(await patched.Content.ReadFromJsonAsync<JsonObject>())!["name"]);
        Assert.Equal("A", await QueryAsync(host, db => db.Products.Select(p => p.Sku).SingleAsync()));

        Assert.Equal(HttpStatusCode.PreconditionFailed, (await SendAsync(host, HttpMethod.Delete, $"/products/{id}", ifMatch: newTag)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(host, HttpMethod.Delete, $"/products/{id}", ifMatch: patched.Headers.ETag!.Tag)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Delete, $"/products/{id}")).StatusCode);
    }

    [Fact]
    public async Task Tenants_see_and_change_only_their_own_rows()
    {
        await using var host = await StartAsync();
        await SeedAsync(host, Product("A", "Acme anvil", 1), Product("G", "Globex gear", 2, tenant: "globex"));
        var globexId = await QueryAsync(host, db => db.Products.Where(p => p.TenantId == "globex").Select(p => p.Id).SingleAsync());

        // A shared endpoint works on the rows of the request's tenant.
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/products").HandledByCrud<CrudProduct>(CrudOperation.List));
        var acme = await (await SendAsync(host, HttpMethod.Get, "/products")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(["Acme anvil"], acme!["items"]!.AsArray().Select(i => (string)i!["name"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Get, "/products", tenant: null)).StatusCode);

        // An endpoint of acme never reaches globex's rows, whatever the key.
        var acmeEndpoints = new[]
        {
            DynamicEndpoint.Get("/items/{id}").HandledByCrud<CrudProduct>(CrudOperation.Get).FromRoute("id", p => p.Integer()),
            DynamicEndpoint.Patch("/items/{id}").HandledByCrud<CrudProduct>(CrudOperation.Patch).FromRoute("id", p => p.Integer()).FromBody("name"),
            DynamicEndpoint.Delete("/items/{id}").HandledByCrud<CrudProduct>(CrudOperation.Delete).FromRoute("id", p => p.Integer()),
            DynamicEndpoint.Post("/items").HandledByCrud<CrudProduct>(CrudOperation.Create).FromBody("sku").FromBody("name"),
        };
        foreach (var endpoint in acmeEndpoints)
        {
            await host.Manager.CreateAsync(endpoint.Build() with { Tenant = "acme" });
        }

        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Get, $"/items/{globexId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Patch, $"/items/{globexId}", body: new { name = "hacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(host, HttpMethod.Delete, $"/items/{globexId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(host, HttpMethod.Post, "/items", body: new { sku = "N", name = "New", tenantId = "globex" })).StatusCode);

        Assert.Equal("Globex gear", await QueryAsync(host, db => db.Products.Where(p => p.Id == globexId).Select(p => p.Name).SingleAsync()));
        Assert.Equal(["acme", "acme", "globex"], await QueryAsync(host, db => db.Products.OrderBy(p => p.TenantId).Select(p => p.TenantId).ToListAsync()));
    }

    [Fact]
    public async Task Entities_without_a_tenant_column_are_off_limits_for_tenants_unless_allowed()
    {
        await using var host = await StartAsync();

        var create = await host.Client.PostAsJsonAsync("/admin/tenants/acme/endpoints",
            DynamicEndpoint.Get("/orders").HandledByCrud<CrudOrder>(CrudOperation.List).Build());
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        var problem = await create.Content.ReadAsStringAsync();
        Assert.Contains("Entity 'orders' isn't available to tenant 'acme'. Available: countries, products.", problem);
        Assert.Contains(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/orders").HandledByCrud<CrudOrder>(CrudOperation.List).Build() with { Tenant = "acme" }),
            e => e.Contains("isn't available to tenant 'acme'"));

        // Shared reference data is fine for every tenant; shared endpoints may use everything.
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync("/admin/tenants/acme/endpoints",
            DynamicEndpoint.Get("/countries").HandledByCrud<CrudCountry>(CrudOperation.List).Build())).StatusCode);
        Assert.Empty(await ConfigErrorsAsync(host, DynamicEndpoint.Get("/orders").HandledByCrud<CrudOrder>(CrudOperation.List)));

        var tenantEntities = await host.Client.GetFromJsonAsync<JsonArray>("/admin/tenants/acme/endpoints/crud/entities");
        Assert.Equal(["countries", "products"], tenantEntities!.Select(e => (string)e!["name"]!));
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/admin/tenants/acme/endpoints/scaffold/crud?entity=orders", null)).StatusCode);
        Assert.Contains("crud", (await host.Client.GetFromJsonAsync<DynamicEndpointsAdminInfo>("/admin/tenants/acme/endpoints/info"))!.Features);
    }

    [Fact]
    public async Task AllowTenants_assigns_an_entity_to_tenants()
    {
        await using var host = await StartAsync(crud => crud
            .Entity<CrudOrder>("orders", e => e.AllFields().AllowTenants("acme"))
            .Entity<CrudProduct>(e => e.Fields(p => p.Name).TenantColumn(p => p.TenantId).AllowTenants("globex")));

        var definition = DynamicEndpoint.Get("/orders").HandledByCrud<CrudOrder>(CrudOperation.List).Build();
        Assert.Empty(await ConfigErrorsAsync(host, definition with { Tenant = "acme" }));
        Assert.NotEmpty(await ConfigErrorsAsync(host, definition with { Tenant = "globex" }));
        var products = DynamicEndpoint.Get("/products").HandledByCrud<CrudProduct>(CrudOperation.List).Build();
        Assert.Empty(await ConfigErrorsAsync(host, products with { Tenant = "globex" }));
        Assert.NotEmpty(await ConfigErrorsAsync(host, products with { Tenant = "acme" }));
        Assert.Empty((await host.Client.GetFromJsonAsync<DynamicEndpointsAdminInfo>("/admin/tenants/initech/endpoints/info"))!.Features);
    }

    [Fact]
    public async Task Interceptors_run_around_writes_and_can_reject_them()
    {
        var log = new List<string>();
        await using var host = await StartAsync(services: s => s.AddScoped<IDynamicCrudInterceptor<CrudProduct>>(_ => new ProductRules(log)));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/products").HandledByCrud<CrudProduct>(CrudOperation.Create).FromBody("sku").FromBody("name"));
        await host.Manager.CreateAsync(DynamicEndpoint.Patch("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Patch).FromRoute("id", p => p.Integer()).FromBody("name"));
        await host.Manager.CreateAsync(DynamicEndpoint.Delete("/products/{id}").HandledByCrud<CrudProduct>(CrudOperation.Delete).FromRoute("id", p => p.Integer()));

        var rejected = await SendAsync(host, HttpMethod.Post, "/products", body: new { sku = "X", name = "forbidden" });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        var errors = (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!;
        Assert.Equal("That name is taken.", (string?)errors["name"]![0]);
        Assert.Equal(0, await QueryAsync(host, db => db.Products.CountAsync()));

        var created = await (await SendAsync(host, HttpMethod.Post, "/products", body: new { sku = "A", name = "Anvil" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(new DateTime(2026, 1, 1), (DateTime)created!["createdAt"]!);
        var id = (int)created["id"]!;
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Patch, $"/products/{id}", body: new { name = "Anvil 2" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(host, HttpMethod.Delete, $"/products/{id}")).StatusCode);
        await QueryAsync(host, db => db.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, 0)));
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(host, HttpMethod.Delete, $"/products/{id}")).StatusCode);

        Assert.Equal(["before create forbidden", "before create Anvil", "after create acme", "before patch Anvil 2", "after patch", "before delete", "before delete", "after delete"], log);
    }

    [Fact]
    public async Task Scaffolding_generates_endpoints_from_the_model_with_a_dry_run_first()
    {
        await using var host = await StartAsync();

        var dryRun = await host.Client.PostAsync("/admin/endpoints/scaffold/crud?entity=products&dryRun=true&routePrefix=/catalog/products", null);
        Assert.Equal(HttpStatusCode.OK, dryRun.StatusCode);
        var preview = await dryRun.Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)preview!["dryRun"]!);
        Assert.Equal(6, (int)preview["created"]!);
        Assert.Equal(["GET /catalog/products", "GET /catalog/products/{id}", "POST /catalog/products", "PUT /catalog/products/{id}", "PATCH /catalog/products/{id}", "DELETE /catalog/products/{id}"],
            preview["operations"]!.AsArray().Select(o => $"{o!["method"]} {o["route"]}"));
        Assert.Empty(await host.Manager.ListAsync());

        var create = preview["operations"]!.AsArray().Single(o => (string)o!["operation"]! == "create")!["definition"]!.Deserialize<DynamicEndpointDefinition>(DynamicEndpointsJson.SerializerOptions)!;
        Assert.False(create.Enabled);
        var sku = create.Parameters.Single(p => p.Name == "sku");
        Assert.Equal((ParameterSource.Body, ParameterType.String, true, 20), (sku.Source, sku.Type, sku.Required, sku.MaxLength));
        var price = create.Parameters.Single(p => p.Name == "price");
        Assert.Equal((ParameterType.Number, false, 99999999.99m), (price.Type, price.Required, price.Maximum));
        Assert.Equal(["Draft", "Active", "Retired"], create.Parameters.Single(p => p.Name == "status").AllowedValues!.Select(v => (string)v!));
        Assert.DoesNotContain(create.Parameters, p => p.Name is "id" or "createdAt" or "tenantId");
        var list = preview["operations"]!.AsArray().Single(o => (string)o!["operation"]! == "list")!["definition"]!.Deserialize<DynamicEndpointDefinition>(DynamicEndpointsJson.SerializerOptions)!;
        Assert.Contains(list.Parameters, p => p.Name == "minPrice");
        Assert.Equal(200m, list.Parameters.Single(p => p.Name == "pageSize").Maximum);

        var scaffolded = await (await host.Client.PostAsync("/admin/endpoints/scaffold/crud?entity=products&routePrefix=/catalog/products&enabled=true&operation=list,get&operation=create", null))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)scaffolded!["succeeded"]!);
        Assert.Equal(3, (int)scaffolded["created"]!);
        Assert.Equal(3, (await host.Manager.ListAsync()).Count);

        var created = await SendAsync(host, HttpMethod.Post, "/catalog/products", body: new { sku = "A-1", name = "Anvil", price = 9.99, status = "Active" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, HttpMethod.Get, created.Headers.Location!.OriginalString)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(host, HttpMethod.Post, "/catalog/products", body: new { sku = new string('x', 21), name = "n" })).StatusCode);
        var page = await (await SendAsync(host, HttpMethod.Get, "/catalog/products?sort=-price&minPrice=5")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(1, (int)page!["total"]!);

        // Existing routes are skipped.
        var again = await (await host.Client.PostAsync("/admin/endpoints/scaffold/crud?entity=products&routePrefix=catalog/products", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(3, (int)again!["skipped"]!);
        Assert.Equal(3, (int)again["created"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsync("/admin/endpoints/scaffold/crud?entity=categories", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PostAsync("/admin/endpoints/scaffold/crud?entity=products&operation=drop", null)).StatusCode);
    }

    [Fact]
    public async Task Scaffolding_in_a_tenant_admin_API_creates_endpoints_of_that_tenant()
    {
        await using var host = await StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/catalog").HandledBy("echo").Build() with { Tenant = "globex" });

        var result = await (await host.Client.PostAsync("/admin/tenants/acme/endpoints/scaffold/crud?entity=products&routePrefix=/catalog&enabled=true&operation=list&operation=get", null))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.True((bool)result!["succeeded"]!);
        Assert.Equal(2, (int)result["created"]!);
        var endpoints = (await host.Manager.ListAsync()).Select(s => s.Definition).Where(d => d.Processor == "ef-crud").ToList();
        Assert.All(endpoints, d => Assert.Equal("acme", d.Tenant));

        await SeedAsync(host, Product("A", "Acme anvil", 1), Product("G", "Globex gear", 2, tenant: "globex"));
        var acme = await (await SendAsync(host, HttpMethod.Get, "/catalog")).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(["Acme anvil"], acme!["items"]!.AsArray().Select(i => (string)i!["name"]!));

        // globex's admin API sees only its own routes: its GET /catalog exists, acme's GET /catalog/{id} doesn't.
        var globex = await (await host.Client.PostAsync("/admin/tenants/globex/endpoints/scaffold/crud?entity=products&routePrefix=/catalog&dryRun=true&operation=list,get", null))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(["Skip", "Create"], globex!["operations"]!.AsArray().Select(o => (string)o!["action"]!));
        Assert.All(globex["operations"]!.AsArray(), o => Assert.Equal("globex", (string?)o!["definition"]!["tenant"]));
    }

    [Fact]
    public async Task The_OpenAPI_document_shows_fields_ETags_and_preconditions()
    {
        await using var host = await StartAsync();
        await host.Services.GetRequiredService<IDynamicCrudScaffolder>().ScaffoldAsync("orders", new() { Enabled = true });

        var document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");
        var paths = document!["paths"]!;
        var list = paths["/orders"]!["get"]!;
        var item = list["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["items"]!["items"]!;
        Assert.Equal(["id", "customer", "total", "orderedOn", "lastModified"], item["properties"]!.AsObject().Select(p => p.Key));
        Assert.True((bool)item["properties"]!["id"]!["readOnly"]!);
        Assert.Equal("orders", (string?)list["x-dynamic-endpoint"]!["crud"]!["entity"]);

        var create = paths["/orders"]!["post"]!["responses"]!;
        Assert.Equal("201", create.AsObject().First().Key);
        Assert.NotNull(create["201"]!["headers"]!["Location"]);
        Assert.Null(create["200"]);

        var get = paths["/orders/{id}"]!["get"]!["responses"]!;
        Assert.NotNull(get["404"]);
        Assert.Null(get["200"]!["headers"]?["ETag"]); // orders have no concurrency token

        await host.Services.GetRequiredService<IDynamicCrudScaffolder>().ScaffoldAsync("products", new() { Enabled = true, RoutePrefix = "/products" });
        document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");
        var product = document!["paths"]!["/products/{id}"]!;
        Assert.NotNull(product["get"]!["responses"]!["200"]!["headers"]!["ETag"]);
        Assert.Contains(product["put"]!["parameters"]!.AsArray(), p => (string?)p!["name"] == "If-Match");
        Assert.NotNull(product["patch"]!["responses"]!["412"]);
        Assert.NotNull(product["delete"]!["responses"]!["204"]);
        Assert.NotNull(product["delete"]!["responses"]!["412"]);
        Assert.Equal("string", (string?)product["get"]!["responses"]!["200"]!["content"]!["application/json"]!["schema"]!["properties"]!["status"]!["type"]);
    }

    [Fact]
    public async Task A_configuration_that_does_not_match_the_model_fails_on_start()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var host = await StartAsync(crud => crud
                .Entity<CrudProduct>(e => e.Fields(p => p.Name).Sortable(p => p.Price).TenantColumn("Missing"))
                .Entity<CrudOrderLine>(e => e.Fields(l => l.Item).Operations(CrudOperations.All)));
        });

        Assert.Contains("'price' is filterable or sortable but not an exposed field", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tenant column 'Missing'", error.Message);
        Assert.Contains("'orderLines' (CrudOrderLine): Only entities with a single-property key", error.Message);
        Assert.Throws<InvalidOperationException>(() => new DynamicCrudBuilder<CrudDbContext>()
            .Entity<CrudProduct>("items", e => e.AllFields()).Entity<CrudOrder>("Items", e => e.AllFields()));
    }

    public void Dispose()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private sealed class ProductRules(List<string> log) : IDynamicCrudInterceptor<CrudProduct>
    {
        public ValueTask BeforeCreateAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add($"before create {context.Entity.Name}");
            if (context.Entity.Name == "forbidden")
            {
                context.Reject("name", "That name is taken.");
            }

            context.Entity.CreatedAt = new DateTime(2026, 1, 1);
            context.Entity.Stock = 3;
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterCreateAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add($"after create {context.Entity.TenantId}");
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeUpdateAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add($"before {context.Operation.ToString().ToLowerInvariant()} {context.Entity.Name}");
            return ValueTask.CompletedTask;
        }

        public ValueTask AfterUpdateAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add($"after {context.Operation.ToString().ToLowerInvariant()}");
            return ValueTask.CompletedTask;
        }

        public ValueTask BeforeDeleteAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add("before delete");
            if (context.Entity.Stock > 0)
            {
                context.Reject("id", "Products in stock can't be deleted.");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask AfterDeleteAsync(DynamicCrudContext<CrudProduct> context)
        {
            log.Add("after delete");
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class CrudDbContext(DbContextOptions<CrudDbContext> options) : DbContext(options)
{
    public DbSet<CrudProduct> Products => Set<CrudProduct>();

    public DbSet<CrudCategory> Categories => Set<CrudCategory>();

    public DbSet<CrudOrder> Orders => Set<CrudOrder>();

    public DbSet<CrudOrderLine> OrderLines => Set<CrudOrderLine>();

    public DbSet<CrudCountry> CountryList => Set<CrudCountry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CrudProduct>(e =>
        {
            e.Property(p => p.Sku).HasMaxLength(20);
            e.Property(p => p.Name).HasMaxLength(100);
            // SQLite can't compare or sort decimals; a REAL column keeps the example simple.
            e.Property(p => p.Price).HasPrecision(10, 2).HasConversion<double>();
            e.Property(p => p.Version).IsConcurrencyToken();
        });
        modelBuilder.Entity<CrudOrder>(e =>
        {
            e.Property(o => o.Customer).HasMaxLength(50);
            e.Property(o => o.Total).HasPrecision(10, 2).HasConversion<double>();
            e.Property<DateTime?>("LastModified");
        });
        modelBuilder.Entity<CrudOrderLine>().HasKey(l => new { l.OrderId, l.Line });
        modelBuilder.Entity<CrudCountry>().Property(c => c.Code).HasMaxLength(2);
    }
}

public enum CrudProductStatus
{
    Draft,
    Active,
    Retired,
}

public sealed class CrudProduct
{
    public int Id { get; set; }

    public string Sku { get; set; } = "";

    public string Name { get; set; } = "";

    public decimal Price { get; set; }

    public int Stock { get; set; }

    public CrudProductStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public string TenantId { get; set; } = "";

    public string? InternalNote { get; set; }

    public Guid Version { get; set; }

    public int? CategoryId { get; set; }

    public CrudCategory? Category { get; set; }
}

public sealed class CrudCategory
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class CrudOrder
{
    public Guid Id { get; set; }

    public string Customer { get; set; } = "";

    public decimal Total { get; set; }

    public string? InternalNote { get; set; }

    public DateOnly OrderedOn { get; set; }

    public List<CrudOrderLine> Lines { get; set; } = [];
}

public sealed class CrudOrderLine
{
    public Guid OrderId { get; set; }

    public int Line { get; set; }

    public string Item { get; set; } = "";
}

[DynamicEntity("countries")]
public sealed class CrudCountry
{
    [Key]
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";
}
