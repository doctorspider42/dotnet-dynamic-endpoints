using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.BuiltInProcessors;

/// <summary>
/// Stand-ins for the services the built-in processors talk to, mapped as ordinary static endpoints under the reserved
/// <c>/fake</c> prefix of this very application – so http-forward and webhook work offline. In real life these are other services.
/// </summary>
public static class FakeUpstream
{
    public static RouteGroupBuilder MapFakeUpstream(this IEndpointRouteBuilder endpoints)
    {
        var fake = endpoints.MapGroup("/fake").WithTags("Fake upstream (the targets of http-forward and webhook)");

        // A "CRM" for http-forward. It wants the API key the forward endpoint takes from the configuration ({config:Upstream:ApiKey}).
        fake.MapGet("/crm/customers/{id:int}", (int id, HttpRequest request, IConfiguration configuration) =>
        {
            if (request.Headers["X-Api-Key"] != configuration["Upstream:ApiKey"])
            {
                return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "The CRM wants its API key.");
            }

            return id > 100
                ? Results.NotFound()
                : Results.Ok(new { id, fullName = $"Customer {id}", email = $"customer{id}@example.com", tier = id % 2 == 0 ? "gold" : "silver" });
        });

        // A webhook receiver: checks the HMAC signature (Webhooks:SigningSecret) and keeps what it got.
        fake.MapPost("/webhooks", async (HttpRequest request, WebhookInbox inbox, IConfiguration configuration) =>
        {
            var delivery = await ReceiveAsync(request, configuration);
            inbox.Received.Enqueue(delivery);
            return Results.Ok();
        });

        // A flaky receiver: answers 503 to the first attempt of every delivery, so the webhook processor retries.
        fake.MapPost("/webhooks/flaky", async (HttpRequest request, WebhookInbox inbox, IConfiguration configuration) =>
        {
            var delivery = await ReceiveAsync(request, configuration);
            if (inbox.Attempts.AddOrUpdate(delivery.DeliveryId, 1, (_, n) => n + 1) == 1)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            inbox.Received.Enqueue(delivery with { Attempts = inbox.Attempts[delivery.DeliveryId] });
            return Results.Ok();
        });

        fake.MapGet("/webhooks", (WebhookInbox inbox) => inbox.Received.Reverse());

        return fake;
    }

    private static async Task<ReceivedWebhook> ReceiveAsync(HttpRequest request, IConfiguration configuration)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        var secret = configuration["Webhooks:SigningSecret"] ?? "";
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body)));
        return new ReceivedWebhook(
            request.Headers["X-Webhook-Delivery"].ToString(),
            SignatureValid: request.Headers["X-Webhook-Signature"] == expected,
            Payload: JsonNode.Parse(body),
            Attempts: 1,
            ReceivedAt: DateTimeOffset.UtcNow);
    }
}

public sealed record ReceivedWebhook(string DeliveryId, bool SignatureValid, JsonNode? Payload, int Attempts, DateTimeOffset ReceivedAt);

/// <summary>What the fake webhook receivers got (in memory).</summary>
public sealed class WebhookInbox
{
    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    public ConcurrentDictionary<string, int> Attempts { get; } = new();
}
