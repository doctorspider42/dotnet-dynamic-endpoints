using System.ComponentModel.DataAnnotations;

namespace DynamicEndpoints.Samples.CustomProcessors.Validators;

public sealed class CsvConfig
{
    /// <summary>The header the CSV must start with, e.g. <c>["sku", "quantity"]</c>.</summary>
    [MinLength(1, ErrorMessage = "'columns' needs at least one column.")]
    public List<string> Columns { get; set; } = [];

    [Range(1, 10_000)]
    public int MaxRows { get; set; } = 100;
}

/// <summary>
/// Handing work on: the validator parses the CSV text once, reports errors with line numbers, and hands the parsed rows to the
/// processor with <see cref="DynamicValidationContext.SetParsedValue"/> – the processor reads them with
/// <c>request.GetParsedValue&lt;T&gt;("csv")</c> instead of parsing again. <c>context.Items</c> carries anything else along.
/// </summary>
[DynamicValidator("csv",
    Description = "Parses a CSV text parameter with the configured header; hands the rows on to the processor.",
    ConfigurationExample = """{ "columns": ["sku", "quantity"], "maxRows": 100 }""",
    Targets = DynamicValidatorTargets.Parameter,
    ParameterTypes = [ParameterType.String])]
public sealed class CsvValidator : DynamicValidator<CsvConfig>
{
    protected override ValueTask ValidateAsync(DynamicValidationContext context, CsvConfig configuration)
    {
        var lines = (context.GetValue<string>() ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0 || !lines[0].Split(',').Select(c => c.Trim()).SequenceEqual(configuration.Columns, StringComparer.OrdinalIgnoreCase))
        {
            context.AddError(null, $"The first line must be the header: {string.Join(',', configuration.Columns)}.", "csv.header");
            return ValueTask.CompletedTask;
        }

        if (lines.Length - 1 > configuration.MaxRows)
        {
            context.AddError(null, $"At most {configuration.MaxRows} rows.", "csv.rows");
            return ValueTask.CompletedTask;
        }

        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = lines[i].Split(',').Select(c => c.Trim()).ToArray();
            if (cells.Length != configuration.Columns.Count)
            {
                context.AddError(null, $"Line {i + 1} has {cells.Length} values instead of {configuration.Columns.Count}.", "csv.line");
                continue;
            }

            rows.Add(configuration.Columns.Zip(cells).ToDictionary(c => c.First, c => c.Second));
        }

        context.SetParsedValue(rows);                 // for the validated parameter, "csv"
        context.Items["csv.lines"] = lines.Length;    // anything else – shared by filters, validators and the processor
        return ValueTask.CompletedTask;
    }
}
