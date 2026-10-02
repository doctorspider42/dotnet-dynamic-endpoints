namespace DynamicEndpoints.Samples.CustomProcessors.Processors;

/// <summary>Takes the rows <see cref="Validators.CsvValidator"/> parsed – no second parse.</summary>
[DynamicProcessor("csv-summary", Description = "Summarizes the rows a 'csv' validator parsed from the 'csv' parameter.")]
public sealed class CsvSummaryProcessor : IDynamicEndpointProcessor
{
    public Task<IResult> ProcessAsync(DynamicRequest request)
    {
        var rows = request.GetParsedValue<List<Dictionary<string, string>>>("csv") ?? [];
        return Task.FromResult(Results.Ok(new
        {
            rows = rows.Count,
            lines = request.Items["csv.lines"],
            records = rows,
        }));
    }
}
