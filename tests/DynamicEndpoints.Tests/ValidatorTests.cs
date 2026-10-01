using System.Net;
using System.Net.Http.Json;
using FluentValidation;
using DynamicEndpoints.FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using static DynamicEndpoints.Tests.RequestHandlingTests;

namespace DynamicEndpoints.Tests;

public sealed class ValidatorTests
{
    [Fact]
    public async Task Parameter_validators_run_alongside_built_in_checks_and_report_under_request_names()
    {
        var calls = 0;
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddValidator("even", ctx =>
            {
                calls++;
                if (ctx.GetValue<long>() % 2 != 0)
                {
                    ctx.AddError("Must be even.");
                }

                return ValueTask.CompletedTask;
            }, DynamicValidatorTargets.Parameter));
        await host.Manager.CreateAsync(DynamicEndpoint.Get("/numbers")
            .HandledBy("echo")
            .FromQuery("count", p => p.Integer().Range(1, 100).ValidatedBy("even"))
            .FromHeader("size", p => p.BindFrom("X-Size").Integer().ValidatedBy("even"))
            .FromQuery("name", p => p.Required()));

        // Built-in errors and custom errors come back together.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/numbers?count=3");
        request.Headers.Add("X-Size", "5");
        var errors = await ReadErrorsAsync(await host.Client.SendAsync(request));
        Assert.Equal(["Must be even."], errors["count"]);
        Assert.Equal(["Must be even."], errors["X-Size"]);
        Assert.Contains("name", errors.Keys);

        // A value that already failed a built-in constraint is not passed to custom validators; absent ones neither.
        calls = 0;
        var outOfRange = await ReadErrorsAsync(await host.Client.GetAsync("/numbers?count=101&name=x"));
        Assert.DoesNotContain("Must be even.", outOfRange["count"]);
        Assert.Equal(0, calls);

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/numbers?count=4&name=x")).StatusCode);
    }

    [Fact]
    public async Task Request_validators_get_configuration_and_run_only_for_otherwise_valid_requests()
    {
        await using var host = await TestHost.StartAsync(configure: b => b.AddValidator<BlockedWordsValidator>());
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/comments")
            .HandledBy("echo")
            .FromBody("text", p => p.Required().MaxLength(50))
            .FromBody("author", p => p.Required())
            .ValidatedBy("blocked-words", new { words = new[] { "spam" }, field = "text" }));

        var blocked = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/comments", new { text = "buy SPAM now", author = "bot" }));
        Assert.Equal(["Contains a blocked word: spam."], blocked["text"]);

        var invalid = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/comments", new { text = "spam" }));
        Assert.Equal(["author"], invalid.Keys); // request validators skipped while parameters are invalid

        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/comments", new { text = "hello", author = "me" })).StatusCode);
    }

    [Fact]
    public async Task Validator_references_are_checked_when_saving()
    {
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddValidator<BlockedWordsValidator>()
            .AddValidator("positive", _ => ValueTask.CompletedTask, DynamicValidatorTargets.Parameter));

        var result = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x")
            .HandledBy("echo")
            .FromBody("a", p => p.ValidatedBy("missing").ValidatedBy("blocked-words", new { words = new[] { "x" } }))
            .ValidatedBy("positive")
            .ValidatedBy("blocked-words", new { wrods = new[] { "typo" } }));

        Assert.Contains("Unknown validator", result.Errors["parameters[0].validators[0].name"].Single());
        Assert.Contains("attach it to the endpoint", result.Errors["parameters[0].validators[1].name"].Single());
        Assert.Contains("attach it to a parameter", result.Errors["validators[0].name"].Single());
        Assert.Contains("validators[1].config", result.Errors.Keys);
        Assert.Contains(host.Manager.Validators, v => v is { Name: "blocked-words", ForRequests: true, ForParameters: false });
    }

    [Fact]
    public async Task FluentValidation_validators_work_for_requests_and_parameters()
    {
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddFluentValidator<TransferValidator>()
            .AddFluentValidator<AccountNumberValidator>("account"));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/transfers")
            .HandledBy("echo")
            .FromBody("from", p => p.Required().ValidatedBy("account"))
            .FromBody("to", p => p.Required().ValidatedBy("account"))
            .FromBody("amount", p => p.Number().Required())
            .FromBody("executeOn", p => p.Date().Required())
            .ValidatedBy("transfer"));

        Assert.Contains(host.Manager.Validators, v => v is { Name: "transfer", ForRequests: true, ForParameters: false });
        Assert.Contains(host.Manager.Validators, v => v is { Name: "account", ForRequests: false, ForParameters: true });

        var badAccount = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/transfers",
            new { from = "ACC-1", to = "nope", amount = 10, executeOn = "2030-01-01" }));
        Assert.Equal(["to"], badAccount.Keys);
        Assert.Equal(["Account numbers start with ACC-."], badAccount["to"]);

        var badTransfer = await ReadErrorsAsync(await host.Client.PostAsJsonAsync("/transfers",
            new { from = "ACC-1", to = "ACC-1", amount = 5000, executeOn = "2030-01-01" }));
        Assert.Equal(["Cannot transfer to the same account."], badTransfer["to"]);
        Assert.Equal(["Limit is 1000."], badTransfer["amount"]);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.PostAsJsonAsync("/transfers",
            new { from = "ACC-1", to = "ACC-2", amount = 10, executeOn = "2030-01-01" })).StatusCode);
    }

    public sealed class BlockedWordsConfig
    {
        public List<string> Words { get; set; } = [];

        public string Field { get; set; } = "text";
    }

    [DynamicValidator("blocked-words", Targets = DynamicValidatorTargets.Request)]
    public sealed class BlockedWordsValidator : DynamicValidator<BlockedWordsConfig>
    {
        protected override ValueTask ValidateAsync(DynamicValidationContext context, BlockedWordsConfig configuration)
        {
            var text = context.Get<string>(configuration.Field) ?? string.Empty;
            foreach (var word in configuration.Words.Where(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            {
                context.AddError(configuration.Field, $"Contains a blocked word: {word}.");
            }

            return ValueTask.CompletedTask;
        }

        protected override IEnumerable<string> Validate(BlockedWordsConfig configuration)
        {
            if (configuration.Words.Count == 0)
            {
                yield return "At least one word is required.";
            }
        }
    }

    public sealed record Transfer(string From, string To, decimal Amount, DateOnly ExecuteOn);

    public sealed class TransferValidator : AbstractValidator<Transfer>
    {
        public TransferValidator()
        {
            RuleFor(t => t.To).NotEqual(t => t.From).WithMessage("Cannot transfer to the same account.");
            RuleFor(t => t.Amount).Custom((amount, ctx) =>
            {
                // The dynamic context is available to custom rules (definition, HttpContext, configuration).
                var endpoint = ctx.GetDynamicContext()!.Endpoint;
                if (endpoint.Route == "/transfers" && amount > 1000)
                {
                    ctx.AddFailure("Limit is 1000.");
                }
            });
        }
    }

    public sealed class AccountNumberValidator : AbstractValidator<string>
    {
        public AccountNumberValidator() =>
            RuleFor(a => a).Must(a => a.StartsWith("ACC-", StringComparison.Ordinal)).WithMessage("Account numbers start with ACC-.");
    }
}
