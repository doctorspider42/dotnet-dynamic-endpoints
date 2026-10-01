using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DynamicEndpoints.Validation.Engine;
using Microsoft.Extensions.DependencyInjection;
using static DynamicEndpoints.Tests.RequestHandlingTests;

namespace DynamicEndpoints.Tests;

/// <summary>The built-in JsonLogic evaluator, JSON Schema subset and string formats (no third-party engines).</summary>
public sealed class EngineTests
{
    private const string Data = """
        { "a": 5, "b": "5", "name": "Ola", "empty": "", "zero": 0, "flag": true, "nothing": null,
          "from": "2026-11-01", "to": "2026-11-05", "items": [1, 2, 3], "user": { "tags": ["x", "y"] } }
        """;

    [Theory]
    // truthiness (JavaScript semantics)
    [InlineData("""{"!!": [{"var": "empty"}]}""", "false")]
    [InlineData("""{"!!": [{"var": "zero"}]}""", "false")]
    [InlineData("""{"!!": [{"var": "items"}]}""", "true")]
    [InlineData("""{"!": [{"var": "missing"}]}""", "true")]
    // equality
    [InlineData("""{"==": [{"var": "a"}, {"var": "b"}]}""", "true")]
    [InlineData("""{"===": [{"var": "a"}, {"var": "b"}]}""", "false")]
    [InlineData("""{"==": [1, true]}""", "true")]
    [InlineData("""{"==": [null, 0]}""", "false")]
    [InlineData("""{"!=": [{"var": "name"}, "Ola"]}""", "false")]
    // comparisons – two strings compare lexically, so ISO dates just work
    [InlineData("""{"<": [{"var": "from"}, {"var": "to"}]}""", "true")]
    [InlineData("""{">": [{"var": "from"}, {"var": "to"}]}""", "false")]
    [InlineData("""{"<": ["10", 9]}""", "false")]
    [InlineData("""{"<": [1, {"var": "a"}, 10]}""", "true")]
    [InlineData("""{"<=": [5, {"var": "a"}, 5]}""", "true")]
    [InlineData("""{"<": ["abc", 1]}""", "false")]
    // logic
    [InlineData("""{"and": [true, {"var": "name"}]}""", "\"Ola\"")]
    [InlineData("""{"or": [0, "", {"var": "a"}]}""", "5")]
    [InlineData("""{"if": [{"var": "flag"}, "yes", "no"]}""", "\"yes\"")]
    [InlineData("""{"if": [false, 1, false, 2, 3]}""", "3")]
    // arithmetic
    [InlineData("""{"+": [{"var": "a"}, "2.5"]}""", "7.5")]
    [InlineData("""{"-": [{"var": "a"}]}""", "-5")]
    [InlineData("""{"*": [2, 3, 4]}""", "24")]
    [InlineData("""{"/": [1, 0]}""", "null")]
    [InlineData("""{"%": [7, 4]}""", "3")]
    [InlineData("""{"max": [1, 9, 3]}""", "9")]
    // strings & arrays
    [InlineData("""{"cat": ["Hi ", {"var": "name"}, "!"]}""", "\"Hi Ola!\"")]
    [InlineData("""{"substr": ["jsonlogic", -5]}""", "\"logic\"")]
    [InlineData("""{"substr": ["jsonlogic", 1, -5]}""", "\"son\"")]
    [InlineData("""{"in": ["Spring", "Springfield"]}""", "true")]
    [InlineData("""{"in": [2, {"var": "items"}]}""", "true")]
    [InlineData("""{"var": "user.tags.1"}""", "\"y\"")]
    [InlineData("""{"var": ["nope", "fallback"]}""", "\"fallback\"")]
    [InlineData("""{"missing": ["a", "empty", "nothing", "x"]}""", """["empty","nothing","x"]""")]
    [InlineData("""{"missing_some": [1, ["a", "x"]]}""", "[]")]
    [InlineData("""{"merge": [1, [2, 3], [[4]]]}""", "[1,2,3,[4]]")]
    [InlineData("""{"all": [{"var": "items"}, {">": [{"var": ""}, 0]}]}""", "true")]
    [InlineData("""{"some": [{"var": "items"}, {">": [{"var": ""}, 2]}]}""", "true")]
    [InlineData("""{"none": [[], {"var": ""}]}""", "true")]
    [InlineData("""{"all": [[], true]}""", "false")]
    [InlineData("""{"map": [{"var": "items"}, {"*": [{"var": ""}, 2]}]}""", "[2,4,6]")]
    [InlineData("""{"filter": [{"var": "items"}, {"%": [{"var": ""}, 2]}]}""", "[1,3]")]
    [InlineData("""{"reduce": [{"var": "items"}, {"+": [{"var": "current"}, {"var": "accumulator"}]}, 0]}""", "6")]
    public void JsonLogic_follows_the_reference_semantics(string rule, string expected)
    {
        var result = JsonLogic.Apply(JsonNode.Parse(rule), JsonNode.Parse(Data));

        Assert.True(JsonValues.DeepEquals(JsonNode.Parse(expected), result), $"got {result?.ToJsonString() ?? "null"}");
    }

    [Theory]
    [InlineData("""{"nonsense": [1]}""", "unknown operator 'nonsense'")]
    [InlineData("""{"and": [true, {"foo": 1}]}""", "unknown operator 'foo'")]
    [InlineData("""{"==": [1, 1], "!=": [1, 2]}""", "exactly one operator")]
    public void JsonLogic_rejects_invalid_rules(string rule, string expected) =>
        Assert.Contains(JsonLogic.Validate(JsonNode.Parse(rule)), p => p.Contains(expected));

    [Theory]
    [InlineData("email", "jan.kowalski+news@example.co.uk", true)]
    [InlineData("email", "no-at-sign.example.com", false)]
    [InlineData("email", "two@@example.com", false)]
    [InlineData("email", "x@localhost", false)]
    [InlineData("email", "a..b@example.com", false)]
    [InlineData("uri", "https://example.com/a?b=c", true)]
    [InlineData("uri", "/relative/path", false)]
    [InlineData("phone", "+48123456789", true)]
    [InlineData("phone", "123456789", false)]
    [InlineData("ipv4", "192.168.0.1", true)]
    [InlineData("ipv4", "256.1.1.1", false)]
    [InlineData("ipv4", "1", false)]
    [InlineData("ipv6", "2001:db8::1", true)]
    [InlineData("ipv6", "192.168.0.1", false)]
    [InlineData("time", "09:30", true)]
    [InlineData("time", "25:00", false)]
    [InlineData("date", "2026-02-28", true)]
    [InlineData("date", "2026-02-30", false)]
    [InlineData("date-time", "2026-01-31T12:00:00Z", true)]
    [InlineData("date-time", "2026-01-31T12:00:00", false)]
    [InlineData("uuid", "0198f1c2-3d4e-7f00-8a1b-2c3d4e5f6a7b", true)]
    [InlineData("uuid", "not-a-guid", false)]
    public void String_formats(string format, string value, bool valid) =>
        Assert.Equal(valid, StringFormats.Check(format, value) is null);

    [Fact]
    public void Schema_subset_validates_nested_structures_and_reports_paths()
    {
        var schema = JsonNode.Parse("""
            { "type": "object", "required": ["city"], "additionalProperties": false,
              "properties": {
                "city": { "type": "string", "minLength": 2 },
                "zip": { "type": "string", "format": "uuid" },
                "lines": { "type": "array", "maxItems": 2, "uniqueItems": true, "items": { "type": "string", "maxLength": 3 } } } }
            """)!.AsObject();
        var errors = new List<string>();

        JsonSchemaLite.Validate(schema, JsonNode.Parse("""{ "zip": "x", "lines": ["abcd", "a", "a"], "extra": 1 }"""),
            (path, message) => errors.Add($"{string.Join('/', path)}: {message}"));

        Assert.Contains(errors, e => e.StartsWith("city: This field is required"));
        Assert.Contains(errors, e => e.StartsWith("zip: Must be a UUID"));
        Assert.Contains(errors, e => e.StartsWith("lines: Must contain at most 2 items"));
        Assert.Contains(errors, e => e.StartsWith("lines/0: Must be at most 3 characters"));
        Assert.Contains(errors, e => e.StartsWith("lines/2: Duplicate item"));
        Assert.Contains(errors, e => e.StartsWith("extra: Unknown field"));
    }

    [Fact]
    public void Schema_subset_rejects_unsupported_keywords_instead_of_ignoring_them()
    {
        var problems = JsonSchemaLite.CheckSupported(JsonNode.Parse("""
            { "type": "object", "properties": { "a": { "oneOf": [{ "type": "string" }] }, "b": { "format": "credit-card" } } }
            """));

        Assert.Contains(problems, p => p.Contains("unsupported keyword 'oneOf'"));
        Assert.Contains(problems, p => p.Contains("unknown format"));
    }

    [Fact]
    public async Task Formats_are_validated_and_documented()
    {
        await using var host = await TestHost.StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/contacts")
            .HandledBy("echo")
            .FromBody("email", p => p.Email().Required())
            .FromBody("phones", p => p.ArrayOf(ParameterType.String).Format(ParameterFormat.Phone))
            .FromQuery("callback", p => p.Uri()));

        var errors = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/contacts?callback=nope",
            new { email = "not-an-email", phones = new[] { "+48123456789", "123" } }));
        Assert.Contains("e-mail", errors["email"].Single());
        Assert.Contains("E.164", errors["phones[1]"].Single());
        Assert.Contains("absolute URI", errors["callback"].Single());

        var ok = await host.Client.PostAsJsonAsync("/contacts?callback=https://example.com/hook", new { email = "ola@example.com" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");
        var body = document!["paths"]!["/contacts"]!["post"]!["requestBody"]!["content"]!["application/json"]!["schema"]!;
        Assert.Equal("email", body["properties"]!["email"]!["format"]!.GetValue<string>());

        var invalid = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("echo").FromQuery("n", p => p.Integer().Format(ParameterFormat.Email)));
        Assert.Contains("parameters[0].format", invalid.Errors.Keys);
    }

    [Fact]
    public async Task Custom_schemas_with_unsupported_keywords_are_rejected_on_save()
    {
        await using var host = await TestHost.StartAsync();

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x")
            .HandledBy("echo")
            .FromBody("address", p => p.Object("""{ "properties": { "city": { "anyOf": [{ "type": "string" }] } } }""")));

        Assert.Contains("unsupported keyword 'anyOf'", result.Errors["parameters[0].schema"].Single());
    }

    [Fact]
    public async Task Processors_and_validators_can_be_referenced_by_type()
    {
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddValidator<ValidatorTests.BlockedWordsValidator>()
            .AddFluentValidator<ValidatorTests.AccountNumberValidator>());

        var definition = DynamicEndpoint.Post("/typed")
            .HandledBy<GreetingProcessor, GreetingConfig>(new() { Greeting = "Hej" })
            .FromBody("name", p => p.Required().ValidatedBy<ValidatorTests.AccountNumberValidator>())
            .ValidatedBy<ValidatorTests.BlockedWordsValidator, ValidatorTests.BlockedWordsConfig>(new() { Words = ["spam"], Field = "name" })
            .Build();

        Assert.Equal("greeting", definition.Processor);                        // from [DynamicProcessor("greeting")]
        Assert.Equal("account-number", definition.Parameters[0].Validators![0].Name); // derived from the FluentValidation type name
        Assert.Equal("blocked-words", definition.Validators[0].Name);

        await host.Manager.CreateAsync(definition);
        Assert.Equal("Hej, ACC-1!", await (await host.Client.PostAsJsonAsync("/typed", new { name = "ACC-1" })).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Parameter_validators_must_accept_the_parameter_type()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddFluentValidator<ValidatorTests.AccountNumberValidator>("account"));

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x")
            .HandledBy("echo")
            .FromQuery("n", p => p.Integer().ValidatedBy("account")));

        Assert.Contains("accepts String/Date/DateTime/Guid parameters, but 'n' is Integer", result.Errors["parameters[0].validators[0].name"].Single());
    }
}
