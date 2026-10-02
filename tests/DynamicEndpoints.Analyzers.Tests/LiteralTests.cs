using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing.Patterns;
using static DynamicEndpoints.Analyzers.Tests.AnalyzerHarness;

namespace DynamicEndpoints.Analyzers.Tests;

/// <summary>DE0006 – DE0010: literal routes, patterns and rules.</summary>
public sealed class LiteralTests
{
    // ------------------------------------------------------------------ routes

    [Theory]
    [InlineData("/orders/{id:int}")]
    [InlineData("/files/{**path}")]
    [InlineData("/files/{*path}")]
    [InlineData("/files/{**path}/")]
    [InlineData("/a/{b?}")]
    [InlineData("/a/{b?}/c")]
    [InlineData("/api/v{version:int}/x")]
    [InlineData(@"/x/{id:regex(^\d{{3}}$)}")]
    [InlineData("/a/{b:regex(^[a-z]{{2,3}}$)}")]
    [InlineData("/a/{b:regex(a/b)}")]
    [InlineData("/a/{b:regex(a?b)}")]
    [InlineData("/a/{b:int:min(1)}")]
    [InlineData("/a/{b:min(1)?}")]
    [InlineData("/a/{b=5}")]
    [InlineData("/a/{b=x?y}")]
    [InlineData("/a/{b}.{c?}")]
    [InlineData("/a/{b}-{c}")]
    [InlineData("/a/{{b}}")]
    [InlineData("/a/{b}{{c}}")]
    [InlineData("/a/")]
    [InlineData("a/b")]
    [InlineData("~/a")]
    [InlineData("/a/{b-c}")]
    [InlineData("/a/{ b }")]
    [InlineData("{a}/{b?}/{c?}")]
    public void Valid_route_templates_pass_and_are_accepted_by_ASP_NET_Core(string route)
    {
        Assert.Null(RouteTemplateChecker.Check(route));
        RoutePatternFactory.Parse(route); // the oracle agrees
    }

    [Theory]
    [InlineData("/a//b", "twice")]
    [InlineData("//", "twice")]
    [InlineData("/a/{b}//", "twice")]
    [InlineData("/a/{}", "no name")]
    [InlineData("/a/{?}", "no name")]
    [InlineData("/a/{**}", "no name")]
    [InlineData("/a/{b", "no matching '}'")]
    [InlineData("/a/b}", "no matching '{'")]
    [InlineData("/a/{b}}", "no matching '}'")]
    [InlineData("/a/{b:regex(})}", "no matching '{'")]
    [InlineData("/a/{b{c}", "escaped")]
    [InlineData(@"/a/{b:regex(\{)}", "escaped")]
    [InlineData("/a/{b*}", "invalid")]
    [InlineData("/a/{b?c}", "invalid")]
    [InlineData("/a/{?b}", "invalid")]
    [InlineData("/a/{b??}", "invalid")]
    [InlineData("/a/{***p}", "invalid")]
    [InlineData("/a/{b/c}", "invalid")]
    [InlineData("/a/{b}/{B}", "more than once")]
    [InlineData("/a/{*p}/c", "last segment")]
    [InlineData("/a/{*p}/{*q}", "last segment")]
    [InlineData("/a/{*p?}", "cannot be marked optional")]
    [InlineData("/a/x{*p}", "only content")]
    [InlineData("/a/{*p}.x", "only content")]
    [InlineData("/a/{b}{c}", "consecutive")]
    [InlineData("/a/{b:int}{c}", "consecutive")]
    [InlineData("/a/{b?}.x", "end of its segment")]
    [InlineData("/a/x.{b?}.y", "end of its segment")]
    [InlineData("/a/x.{b?}", "only a period")]
    [InlineData("/a/x-{b?}", "only a period")]
    [InlineData("/a/{b=5?}", "default value")]
    [InlineData("/a?x=1", "'?'")]
    [InlineData("/a/{b}?", "'?'")]
    [InlineData("~a", "'~'")]
    public void Invalid_route_templates_are_reported_and_rejected_by_ASP_NET_Core(string route, string problem)
    {
        Assert.Contains(problem, RouteTemplateChecker.Check(route));
        Assert.ThrowsAny<Exception>(() => RoutePatternFactory.Parse(route));
    }

    [Fact]
    public async Task Route_literals_are_checked_on_every_http_method()
    {
        await NoDiagnosticsAsync(InMethod("""
            const string prefix = "/api";
            var route = "/a//b"; // not a constant – not checked
            _ = DynamicEndpoint.Get("/orders/{id:int}");
            _ = DynamicEndpoint.Post(prefix + "/orders");
            _ = DynamicEndpoint.Put(route);
            """));

        var findings = await AnalyzeAsync(InMethod("""
            _ = DynamicEndpoint.Get("/a//b");
            _ = DynamicEndpoint.Post("/a/{b");
            _ = DynamicEndpoint.Put("/a/{id}/{ID}");
            _ = DynamicEndpoint.Patch(@"/a/{*rest}/b");
            _ = DynamicEndpoint.Delete("/");
            """));
        Assert.Equal(["\"/a//b\"", "\"/a/{b\"", "\"/a/{id}/{ID}\"", "@\"/a/{*rest}/b\"", "\"/\""], findings.Select(f => f.Text));
        Assert.All(findings, f => Assert.Equal("DE0006", f.Id));
        Assert.Contains("application root", findings[^1].Message);
    }

    // ------------------------------------------------------------------ patterns

    [Theory]
    [InlineData(@"^\d+$")]
    [InlineData("^[A-Za-zÀ-ž-]+$")]
    [InlineData(@"\\1")]
    [InlineData(@"[\1]")]
    [InlineData(@"\(?=x\)")]
    [InlineData(@"[(?=]")]
    [InlineData(@"[]a](?:x)")]
    [InlineData(@"(?<name>a)(?'other'b)")]
    [InlineData("(?i)abc")]
    [InlineData(@"\p{L}+\b")]
    [InlineData("[a-z-[aeiou]](?:y)")]
    public void Patterns_supported_by_the_non_backtracking_engine_pass(string pattern)
    {
        Assert.Null(RegexChecker.FindSyntaxError(pattern));
        Assert.Null(RegexChecker.FindUnsupportedConstruct(pattern));
        _ = new Regex(pattern, RegexOptions.NonBacktracking); // the oracle agrees
    }

    [Theory]
    [InlineData(@"(a)\1", "backreference")]
    [InlineData(@"(?<n>a)\k<n>", "named backreference")]
    [InlineData(@"(?<n>a)\k'n'", "named backreference")]
    [InlineData("(?=a)", "positive lookaround")]
    [InlineData("(?<=a)b", "positive lookaround")]
    [InlineData("(?!a)", "negative lookaround")]
    [InlineData("(?<!a)b", "negative lookaround")]
    [InlineData("(?>a)", "atomic group")]
    [InlineData("(?(a)b|c)", "conditional")]
    [InlineData("(?<n>a)(?(n)b|c)", "conditional")]
    [InlineData("(?<n>a)(?<x-n>b)", "balancing group")]
    [InlineData(@"\Gabc", @"\G")]
    [InlineData(@"[x](?=y)", "positive lookaround")]
    public void Constructs_unsupported_by_the_non_backtracking_engine_are_found(string pattern, string construct)
    {
        Assert.Null(RegexChecker.FindSyntaxError(pattern));
        Assert.Contains(construct, RegexChecker.FindUnsupportedConstruct(pattern));
        Assert.Throws<NotSupportedException>(() => new Regex(pattern, RegexOptions.NonBacktracking));
    }

    [Fact]
    public async Task Pattern_literals_are_checked()
    {
        await NoDiagnosticsAsync(InMethod("""
            _ = DynamicEndpoint.Get("/a").FromQuery("q", p => p.String().Pattern("^[a-z0-9-]{3,32}$"));
            """));

        var findings = await AnalyzeAsync(InMethod("""
            _ = DynamicEndpoint.Get("/a")
                .FromQuery("q", p => p.Pattern("(unclosed"))
                .FromQuery("r", p => p.Pattern(@"^(\w)\1$"));
            """));
        Assert.Equal([("DE0007", "\"(unclosed\""), ("DE0008", "@\"^(\\w)\\1$\"")], findings.Select(f => (f.Id, f.Text)));
        Assert.Contains("NonBacktracking", findings[1].Message);
    }

    // ------------------------------------------------------------------ rules

    [Theory]
    [InlineData("""{ "<": [{ "var": "checkIn" }, { "var": "checkOut" }] }""")]
    [InlineData("""{ "or": [{ "!=": [{ "var": "roomType" }, "single"] }, { "==": [{ "var": "guests" }, 1] }] }""")]
    [InlineData("""{ "<=": [1, 2.5e3, -0.5] }""")]
    [InlineData("""{ "in": ["a", ["a", "b"]] }""")]
    [InlineData("""{ "all": [{ "var": "items" }, { ">": [{ "var": "qty" }, 0] }] }""")]
    [InlineData("""{ "if": [true, "yes", null] }""")]
    [InlineData("""  { "!": { "var": "x\"y\\z\/" } }  """)]
    [InlineData("true")]
    [InlineData("[]")]
    public void Valid_rules_pass(string rule)
    {
        Assert.Null(JsonLogicChecker.Check(rule, out _));
        Assert.NotNull(JsonNode.Parse(rule)); // the oracle agrees
    }

    [Theory]
    [InlineData("""{ "<": [1, 2] """)]
    [InlineData("""{ "<": [1, 2,] }""")]
    [InlineData("""{ '<': [1, 2] }""")]
    [InlineData("""{ "==": [01, 1] }""")]
    [InlineData("""{ "==": [1., 1] }""")]
    [InlineData("""{ "==": [nul, 1] }""")]
    [InlineData("""{ "==": [1, 1] } // comment""")]
    [InlineData("""{ "==": ["\x", 1] }""")]
    [InlineData("""{ "==": ["\u00", 1] }""")]
    [InlineData("""{ "var": "a", "var": "b" }""")]
    [InlineData("")]
    public void Invalid_json_is_a_syntax_error(string rule)
    {
        Assert.NotNull(JsonLogicChecker.Check(rule, out var isSyntaxError));
        Assert.True(isSyntaxError);
        Assert.ThrowsAny<Exception>(() => (JsonNode.Parse(rule) as JsonObject)?.Count); // duplicate keys throw once the object is read
    }

    [Theory]
    [InlineData("""{ "like": [{ "var": "a" }, "x%"] }""", "unknown operator 'like'")]
    [InlineData("""{ "and": [{ "==": [1, 1] }, { "regex": ["a", "b"] }] }""", "#/and/1: unknown operator 'regex'")]
    [InlineData("""{ "==": [1, 1], "!=": [1, 2] }""", "exactly one operator")]
    [InlineData("""{ }""", "exactly one operator")]
    [InlineData("""{ "in": ["a", { "x": 1, "y": 2 }] }""", "exactly one operator")]
    [InlineData("null", "needs a condition")]
    public void Invalid_json_logic_is_reported(string rule, string problem)
    {
        Assert.Contains(problem, JsonLogicChecker.Check(rule, out var isSyntaxError));
        Assert.False(isSyntaxError);
    }

    [Fact]
    public async Task Rule_literals_are_checked()
    {
        await NoDiagnosticsAsync(InMethod(""""
            _ = DynamicEndpoint.Post("/a")
                .WithRule("""{ "<": [{ "var": "from" }, { "var": "to" }] }""", "From must be before to.", "to")
                .WithRule(JsonNode.Parse("{}"), "Not checked – not a string.");
            """"));

        var findings = await AnalyzeAsync(InMethod(""""
            _ = DynamicEndpoint.Post("/a")
                .WithRule("{ \"<\": [1, 2] ", "Broken JSON.")
                .WithRule("""{ "between": [1, 2, 3] }""", "Unknown operator.");
            """"));
        Assert.Equal(["DE0009", "DE0010"], findings.Select(f => f.Id));
        Assert.Contains("unknown operator 'between'", findings[1].Message);
    }

    [Fact]
    public void Operators_match_the_runtime_JsonLogic_engine()
    {
        var engine = typeof(DynamicEndpoint).Assembly.GetType("DynamicEndpoints.Validation.Engine.JsonLogic", throwOnError: true)!;
        var operators = (IReadOnlyList<string>)engine.GetField("Operators", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        Assert.Equal(operators.Order(StringComparer.Ordinal), JsonLogicChecker.Operators.Order(StringComparer.Ordinal));
    }
}
