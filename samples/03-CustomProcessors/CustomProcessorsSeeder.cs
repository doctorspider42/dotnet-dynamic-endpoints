using DynamicEndpoints.Samples.CustomProcessors.Processors;
using DynamicEndpoints.Samples.CustomProcessors.Validators;

namespace DynamicEndpoints.Samples.CustomProcessors;

/// <summary>One or two endpoints per processor of this project, seeded into an empty store.</summary>
public sealed class CustomProcessorsSeeder(IDynamicEndpointManager manager) : IDynamicEndpointSeeder
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if ((await manager.ListAsync(cancellationToken)).Count > 0)
        {
            return;
        }

        DynamicEndpointDefinition[] definitions =
        [
            // Typed processors with a configuration: HandledBy<TProcessor, TConfig>(…) – no magic strings.
            DynamicEndpoint.Get("/hello/{name}")
                .Named("Say hello")
                .InGroup("Typed processors")
                .HandledBy<TemplateProcessor, TemplateConfig>(new() { Template = "Hello, {{name}}! Greetings from a runtime-defined endpoint." })
                .FromRoute("name", p => p.String().Length(2, 30).Example("Ada")),

            DynamicEndpoint.Get("/calc/sum")
                .Named("Sum two numbers")
                .InGroup("Typed processors")
                .HandledBy<CalculatorProcessor, CalculatorConfig>(new() { Operation = CalculatorOperation.Sum, Operands = ["a", "b"] })
                .FromQuery("a", p => p.Number().Required().Example(2))
                .FromQuery("b", p => p.Number().Required().Example(40)),

            DynamicEndpoint.Get("/calc/average")
                .Named("Average of numbers")
                .WithDescription("An array query parameter: ?values=1&values=2&values=6.")
                .InGroup("Typed processors")
                .HandledBy<CalculatorProcessor, CalculatorConfig>(new() { Operation = CalculatorOperation.Average })
                .FromQuery("values", p => p.ArrayOf(ParameterType.Number).Required().Items(1, 20)),

            // An inline processor (Program.cs), registered by name.
            DynamicEndpoint.Get("/time")
                .Named("Current time")
                .InGroup("Typed processors")
                .HandledBy("clock")
                .FromQuery("timeZone", p => p.String().Default("UTC").Example("Europe/Warsaw")),

            // A processor with DI and database access: a tiny document store in the app's DbContext.
            DynamicEndpoint.Post("/notes")
                .Named("Create note")
                .InGroup("Notes (collection processor)")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Insert })
                .FromBody("title", p => p.String().Required().Length(3, 100))
                .FromBody("content", p => p.String().MaxLength(2000))
                .FromBody("priority", p => p.Integer().Range(1, 5).Default(3)),

            DynamicEndpoint.Get("/notes")
                .Named("List notes")
                .InGroup("Notes (collection processor)")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.List })
                .FromQuery("limit", p => p.Integer().Range(1, 100).Default(20)),

            DynamicEndpoint.Get("/notes/{id}")
                .Named("Get note")
                .InGroup("Notes (collection processor)")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Get })
                .FromRoute("id", p => p.Guid()),

            // ApiKeyFilter: every DELETE endpoint needs X-Api-Key.
            DynamicEndpoint.Delete("/notes/{id}")
                .Named("Delete note")
                .WithDescription("Needs the X-Api-Key header (ApiKeyFilter).")
                .InGroup("Notes (collection processor)")
                .HandledBy<CollectionProcessor, CollectionConfig>(new() { Collection = "notes", Action = CollectionAction.Delete })
                .FromRoute("id", p => p.Guid()),

            // A multipart/form-data upload: size and content types are checked before the processor runs.
            DynamicEndpoint.Post("/documents")
                .Named("Upload document")
                .WithDescription("A form with a text field and a file (PDF, PNG or JPEG, at most 1 MB).")
                .InGroup("Forms and files")
                .HandledBy<FileInfoProcessor>()
                .FromForm("title", p => p.String().Required().Length(3, 100))
                .FromForm("file", p => p.File(maxSize: 1024 * 1024, "application/pdf", "image/png", "image/jpeg").Required()),

            // Handing work on: the "csv" validator parses the text once, the processor gets the rows.
            DynamicEndpoint.Post("/imports/csv")
                .Named("Import CSV")
                .WithDescription("The 'csv' validator parses the text and hands the rows on to the 'csv-summary' processor.")
                .InGroup("Handing work on")
                .HandledBy<CsvSummaryProcessor>()
                .FromBody("csv", p => p.String().Required().MaxLength(100_000)
                    .ValidatedBy<CsvValidator>(new CsvConfig { Columns = ["sku", "quantity"], MaxRows = 100 })
                    .Example("sku,quantity\nANV-1,2\nROC-2,1")),
        ];

        foreach (var definition in definitions)
        {
            await manager.CreateAsync(definition, cancellationToken);
        }
    }
}
