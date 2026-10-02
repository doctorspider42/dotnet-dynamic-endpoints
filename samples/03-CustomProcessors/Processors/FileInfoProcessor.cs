using System.Security.Cryptography;

namespace DynamicEndpoints.Samples.CustomProcessors.Processors;

/// <summary>
/// Reads an uploaded file: <c>request.GetFile("file")</c> is a plain <see cref="IFormFile"/>, streamed by ASP.NET Core – no base64.
/// The definition already checked its size and content type. In <c>request.Parameters</c> the file is only its metadata.
/// </summary>
[DynamicProcessor("file-info", Description = "Answers with the metadata and the SHA-256 of the uploaded 'file'.")]
public sealed class FileInfoProcessor : IDynamicEndpointProcessor
{
    public async Task<IResult> ProcessAsync(DynamicRequest request)
    {
        var file = request.GetFile("file");
        if (file is null)
        {
            return Results.Problem("The endpoint has no 'file' parameter.", statusCode: StatusCodes.Status500InternalServerError);
        }

        await using var stream = file.OpenReadStream();
        var hash = await SHA256.HashDataAsync(stream, request.RequestAborted);

        return Results.Ok(new
        {
            title = request.Get<string>("title"),
            fileName = file.FileName,
            contentType = file.ContentType,
            length = file.Length,
            sha256 = Convert.ToHexStringLower(hash),
            parameters = request.Parameters,   // { "title": …, "file": { "fileName", "contentType", "length" } }
        });
    }
}
