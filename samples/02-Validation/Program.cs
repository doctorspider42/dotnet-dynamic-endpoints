// 02 – Validation: the four layers (constraints, parameter validators, rules, request validators), error codes, one error
// format for every error, and messages in English or Polish.
//
//   dotnet run --project samples/02-Validation      → http://localhost:5102/admin/

using DynamicEndpoints;
using DynamicEndpoints.Samples.Validation;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

builder.Services
    .AddDynamicEndpoints(options =>
    {
        options.ReservedPrefixes.Add("/swagger");
        options.ReservedPrefixes.Add("/openapi");
        options.OpenApi.Title = "Validation – dynamic endpoints";

        // Localized messages: English and Polish are built in. Pick the language per request (Accept-Language, through
        // UseRequestLocalization below) instead of always using DefaultCulture.
        options.Messages.DefaultCulture = "en";
        options.Messages.UseRequestCulture = true;
        // Any built-in text can be replaced – keys follow the error codes (DynamicValidationMessages.Keys lists them).
        options.Messages.Set("en", "format.phone", "Use the international format, e.g. +48123456789.");
        options.Messages.Set("pl", "format.phone", "Podaj numer w formacie międzynarodowym, np. +48123456789.");
    })
    // C# validators of this project (NipValidator, OrderTotalValidator) and the seeder.
    .AddFromAssemblyContaining<Program>()
    // FluentValidation validators: BookingRequestValidator → "booking-request" (whole request), IbanValidator → "iban" (one parameter).
    .AddFluentValidatorsFromAssemblyContaining<Program>()
    // Answers with what the processor would get: the validated, converted parameters (defaults filled in).
    .AddProcessor("echo", request => Results.Ok(new { accepted = request.Parameters }), "Returns the validated parameters.")
    // Every error in the application's own format (ApiErrorResponseFactory.cs) instead of RFC 9457 problem details.
    .UseErrorResponseFactory<ApiErrorResponseFactory>()
    .UseEntityFrameworkStore<AppDbContext>();

builder.Services.AddOpenApi();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync(); // migrations in real life
}

// The error factory also builds the empty 404/405 responses and unhandled exceptions – except for the admin tooling.
app.UseDynamicEndpointsErrorResponses(o => o.AppliesTo = context =>
    !context.Request.Path.StartsWithSegments("/admin") &&
    !context.Request.Path.StartsWithSegments("/api/admin") &&
    !context.Request.Path.StartsWithSegments("/swagger"));

// Sets CurrentUICulture from Accept-Language (en or pl) – the messages follow it (UseRequestCulture above).
app.UseRequestLocalization(o => o.SetDefaultCulture("en").AddSupportedCultures("en", "pl").AddSupportedUICultures("en", "pl"));

app.MapDynamicEndpoints();
app.MapDynamicEndpointsAdmin("/api/admin/endpoints");   // .RequireAuthorization("admin") in real life
app.MapDynamicEndpointsAdminUI("/admin", adminApiPath: "/api/admin/endpoints", options =>
{
    options.Title = "Validation";
    options.SwaggerUrl = "/swagger";
    options.OpenApiUrl = "/openapi/dynamic.json";
});
app.MapDynamicEndpointsOpenApi("/openapi/dynamic.json");
app.MapOpenApi("/openapi/{documentName}.json");
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/dynamic.json", "Dynamic endpoints");
    options.SwaggerEndpoint("/openapi/v1.json", "Admin API");
});
app.MapGet("/", () => Results.Redirect("/admin/")).ExcludeFromDescription();

app.Run();

public partial class Program;
