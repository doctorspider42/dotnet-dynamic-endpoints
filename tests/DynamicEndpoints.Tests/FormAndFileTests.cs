using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicEndpoints.Tests;

public sealed class FormAndFileTests
{
    [Fact]
    public async Task Multipart_files_and_fields_are_bound_without_any_encoding()
    {
        await using var host = await StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/documents")
            .HandledBy("upload")
            .FromForm("title", p => p.Required().MinLength(2))
            .FromForm("pages", p => p.Integer().Min(1))
            .FromForm("document", p => p.File(maxSize: 1024, "application/pdf", "image/*").Required())
            .WithRule("""{ "<": [{ "var": "document.length" }, 1000] }""", "Rules see file metadata.", "document"));

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Umowa"), "title" },
            { new StringContent("3"), "pages" },
            { File("%PDF-1.7 zażółć", "application/pdf"), "document", "umowa.pdf" },
        };
        var response = await host.Client.PostAsync("/documents", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Umowa", body!["parameters"]!["title"]!.GetValue<string>());
        Assert.Equal(3, body["parameters"]!["pages"]!.GetValue<long>());
        Assert.Equal("umowa.pdf", body["parameters"]!["document"]!["fileName"]!.GetValue<string>());
        Assert.Equal("application/pdf", body["parameters"]!["document"]!["contentType"]!.GetValue<string>());
        Assert.Equal("%PDF-1.7 zażółć", body["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task File_size_type_and_count_limits_are_enforced_with_codes()
    {
        var failures = new List<DynamicValidationFailedContext>();
        await using var host = await StartAsync(configure: b => b.AddFilter(onValidationFailed: c =>
        {
            failures.Add(c);
            return ValueTask.CompletedTask;
        }));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/photos")
            .HandledBy("upload")
            .FromForm("avatar", p => p.File(maxSize: 10, "image/*"))
            .FromForm("gallery", p => p.Files(contentTypes: "image/png").Items(0, 2))
            .FromForm("name", p => p.Required()));

        using var form = new MultipartFormDataContent
        {
            { File(new string('x', 50), "image/png"), "avatar", "big.png" },
            { File("a", "image/png"), "gallery", "1.png" },
            { File("b", "image/gif"), "gallery", "2.gif" },
            { File("c", "image/png"), "gallery", "3.png" },
        };
        var errors = await RequestHandlingTests.ReadErrorsAsync(await host.Client.PostAsync("/photos", form));

        Assert.Equal(["avatar", "gallery", "gallery[1]", "name"], errors.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("10 bytes", errors["avatar"].Single());
        Assert.Contains("image/gif", errors["gallery[1]"].Single());
        var codes = failures.Single().Errors.ToDictionary(e => e.Key, e => e.Code);
        Assert.Equal(DynamicValidationCodes.FileSize, codes["avatar"]);
        Assert.Equal(DynamicValidationCodes.FileType, codes["gallery[1]"]);
        Assert.Equal(DynamicValidationCodes.MaxItems, codes["gallery"]);
        Assert.Equal(DynamicValidationCodes.Required, codes["name"]);
    }

    [Fact]
    public async Task Url_encoded_forms_work_and_wrong_bodies_are_rejected()
    {
        await using var host = await StartAsync(options: o => o.MaxFormBodySize = 2048);
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/contact")
            .HandledBy("upload")
            .FromForm("email", p => p.Email().Required())
            .FromForm("tags", p => p.ArrayOf(ParameterType.String))
            .FromForm("attachment", p => p.File()));

        var ok = await host.Client.PostAsync("/contact", new FormUrlEncodedContent(
            [new("email", "ola@example.com"), new("tags", "a"), new("tags", "b")]));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(["a", "b"], body!["parameters"]!["tags"]!.AsArray().Select(t => t!.GetValue<string>()));

        var json = await host.Client.PostAsJsonAsync("/contact", new { email = "ola@example.com" });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);

        using var tooLarge = new MultipartFormDataContent
        {
            { new StringContent("ola@example.com"), "email" },
            { File(new string('x', 5000), "text/plain"), "attachment", "a.txt" },
        };
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await host.Client.PostAsync("/contact", tooLarge)).StatusCode);

        var empty = await RequestHandlingTests.ReadErrorsAsync(await host.Client.PostAsync("/contact", null));
        Assert.Contains("email", empty.Keys);

        using var textForFile = new MultipartFormDataContent
        {
            { new StringContent("ola@example.com"), "email" },
            { new StringContent("not a file"), "attachment" },
        };
        var wrongKind = await RequestHandlingTests.ReadErrorsAsync(await host.Client.PostAsync("/contact", textForFile));
        Assert.Equal("A file is expected.", wrongKind["attachment"].Single());
    }

    [Fact]
    public async Task Invalid_form_definitions_are_rejected_on_save()
    {
        await using var host = await StartAsync();

        var fileInQuery = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x").HandledBy("upload").FromQuery("f", p => p.File()));
        Assert.Contains("parameters[0].type", fileInQuery.Errors.Keys);

        var mixed = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x").HandledBy("upload").FromBody("a").FromForm("b"));
        Assert.Contains("parameters", mixed.Errors.Keys);

        var formOnGet = await host.Manager.ValidateAsync(DynamicEndpoint.Get("/x").HandledBy("upload").FromForm("a"));
        Assert.Contains("parameters[0].source", formOnGet.Errors.Keys);

        var badLimits = await host.Manager.ValidateAsync(DynamicEndpoint.Post("/x").HandledBy("upload")
            .FromForm("a", p => p.File(maxSize: 0, "pdf").MinLength(3))
            .FromForm("b", p => p.MaxFileSize(10)));
        Assert.Contains("parameters[0].maxFileSize", badLimits.Errors.Keys);
        Assert.Contains("parameters[0].allowedContentTypes", badLimits.Errors.Keys);
        Assert.Contains("parameters[0].type", badLimits.Errors.Keys);
        Assert.Contains("parameters[1].maxFileSize", badLimits.Errors.Keys);
    }

    [Fact]
    public async Task OpenApi_documents_multipart_bodies_with_binary_files()
    {
        await using var host = await StartAsync();
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/documents")
            .HandledBy("upload")
            .FromForm("title", p => p.Required())
            .FromForm("document", p => p.File(contentTypes: "application/pdf").Required())
            .FromForm("scans", p => p.Files()));
        await host.Manager.CreateAsync(DynamicEndpoint.Post("/contact").HandledBy("upload").FromForm("email"));

        var document = await host.Client.GetFromJsonAsync<JsonObject>("/openapi/dynamic.json");

        var upload = document!["paths"]!["/documents"]!["post"]!;
        var content = upload["requestBody"]!["content"]!.AsObject();
        Assert.Equal(["multipart/form-data"], content.Select(c => c.Key));
        var schema = content["multipart/form-data"]!["schema"]!;
        Assert.Equal("binary", schema["properties"]!["document"]!["format"]!.GetValue<string>());
        Assert.Equal("binary", schema["properties"]!["scans"]!["items"]!["format"]!.GetValue<string>());
        Assert.Equal(["title", "document"], schema["required"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal("application/pdf", content["multipart/form-data"]!["encoding"]!["document"]!["contentType"]!.GetValue<string>());
        Assert.NotNull(upload["responses"]!["413"]);

        var contact = document["paths"]!["/contact"]!["post"]!["requestBody"]!["content"]!.AsObject();
        Assert.Contains("application/x-www-form-urlencoded", contact.Select(c => c.Key));
    }

    private static Task<TestHost> StartAsync(
        Action<DynamicEndpointsOptions>? options = null,
        Action<IDynamicEndpointsBuilder>? configure = null) =>
        TestHost.StartAsync(options: options, configure: b =>
        {
            b.AddProcessor("upload", async request =>
            {
                var file = request.Metadata.Parameters
                    .Where(p => p.Type == ParameterType.File)
                    .Select(p => request.GetFile(p.Name))
                    .FirstOrDefault(f => f is not null);
                string? content = null;
                if (file is not null)
                {
                    using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
                    content = await reader.ReadToEndAsync();
                }

                return Results.Ok(new { parameters = request.Parameters, content });
            });
            configure?.Invoke(b);
        });

    private static ByteArrayContent File(string content, string contentType)
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return file;
    }
}
