using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class AssemblyScanningTests
{
    [Fact]
    public async Task Scanning_registers_processors_validators_and_seeders_and_skips_already_registered_types()
    {
        // TestHost registers GreetingProcessor explicitly – scanning must not register it twice (that would throw).
        // Scanning twice must be harmless as well.
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddFromAssemblyContaining<AssemblyScanningTests>()
            .AddFromAssemblyContaining<AssemblyScanningTests>());

        Assert.Single(host.Manager.Processors, p => p.Name == "greeting");
        Assert.Contains(host.Manager.Validators, v => v.Name == "blocked-words");

        // PersistenceTests.PingSeeder (a private nested class) was found and executed on start-up.
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/seeded")).StatusCode);
    }

    [Fact]
    public async Task Filter_excludes_types()
    {
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddFromAssemblyContaining<AssemblyScanningTests>(t => t.Name is not ("PingSeeder" or "BlockedWordsValidator")));

        Assert.DoesNotContain(host.Manager.Validators, v => v.Name == "blocked-words");
        Assert.Empty(await host.Manager.ListAsync());
    }

    [Fact]
    public async Task FluentValidation_scanning_derives_names_and_respects_explicit_registrations()
    {
        await using var host = await TestHost.StartAsync(configure: b => b
            .AddFluentValidator<ValidatorTests.AccountNumberValidator>("account")
            .AddFluentValidatorsFromAssemblyContaining<AssemblyScanningTests>()
            .AddFluentValidatorsFromAssemblyContaining<AssemblyScanningTests>());

        Assert.Contains(host.Manager.Validators, v => v is { Name: "transfer", ForRequests: true });
        Assert.Contains(host.Manager.Validators, v => v is { Name: "account", ForParameters: true });
        Assert.DoesNotContain(host.Manager.Validators, v => v.Name == "account-number");
    }
}
