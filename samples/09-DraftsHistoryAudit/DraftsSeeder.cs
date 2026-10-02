using System.Text.Json.Nodes;

namespace DynamicEndpoints.Samples.DraftsHistoryAudit;

/// <summary>
/// A bit of history to look at: <c>GET /prices/{sku}</c> was created and changed once (revisions 1 and 2), and has a draft with
/// a third change that isn't live yet. <c>GET /promo</c> exists only as a draft, scheduled to publish itself two minutes after
/// the first start. Fixed ids, so the README and the .http file can link to them.
/// </summary>
public sealed class DraftsSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public static readonly Guid PricesId = Guid.Parse("0199c0de-0000-7000-8000-000000000001");
    public static readonly Guid PromoId = Guid.Parse("0199c0de-0000-7000-8000-000000000002");

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0 || (await manager.ListDraftsAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        // Revision 1.
        var prices = await manager.CreateAsync(Prices(price: 10, maxLength: 10, currency: "EUR").WithId(PricesId), cancellationToken);

        // Revision 2: an ordinary update – live right away. Optimistic concurrency: it's based on revision 1.
        prices = await manager.UpdateAsync(Prices(price: 12, maxLength: 20, currency: "EUR").Build() with { Id = PricesId, Revision = prices.Revision }, cancellationToken);

        // A draft based on revision 2: validated like any definition, but routing keeps serving revision 2 until it's published.
        await manager.SaveDraftAsync(
            Prices(price: 15, maxLength: 20, currency: "{{currency}}")
                .FromQuery("currency", p => p.String().OneOf("EUR", "PLN", "USD").Default("EUR"))
                .Build() with { Id = PricesId, Revision = prices.Revision },
            comment: "New price, and a currency parameter",
            cancellationToken: cancellationToken);

        // A new endpoint as a scheduled draft: the scheduler publishes it at PublishAt (checked every ScheduledPublishInterval).
        await manager.SaveDraftAsync(
            DynamicEndpoint.Get("/promo")
                .WithId(PromoId)
                .Named("Promotion")
                .InGroup("Shop")
                .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new() { Body = new JsonObject { ["code"] = "AUTUMN", ["discount"] = 20 } }),
            publishAt: DateTimeOffset.UtcNow.AddMinutes(2),
            comment: "Goes live two minutes after the first start",
            cancellationToken: cancellationToken);
    }

    private static DynamicEndpoint Prices(decimal price, int maxLength, string currency) =>
        DynamicEndpoint.Get("/prices/{sku}")
            .Named("Get price")
            .InGroup("Shop")
            .HandledBy<ResponseTemplateProcessor, ResponseTemplateConfig>(new()
            {
                Body = new JsonObject { ["sku"] = "{{sku}}", ["price"] = price, ["currency"] = currency },
            })
            .FromRoute("sku", p => p.String().Length(3, maxLength).Example("ANV-1"));
}
