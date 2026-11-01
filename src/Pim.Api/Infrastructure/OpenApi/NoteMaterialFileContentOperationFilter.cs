using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// Material downloads return the immutable source bytes, not a JSON envelope. Swashbuckle cannot
/// infer the custom integrity headers, so document the binary response and its binding metadata.
/// </summary>
public sealed class NoteMaterialFileContentOperationFilter : IOperationFilter
{
    private static readonly Regex MaterialFileContent = new(
        @"^/?api/v1/native-notes/versions/\{versionId(?::guid)?\}/files/\{fileIndex(?::int)?\}/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
            description.RelativePath is null ||
            !MaterialFileContent.IsMatch(description.RelativePath))
            return;

        operation.Responses.TryAdd("200", new OpenApiResponse { Description = "Success" });
        var response = operation.Responses["200"];
        response.Content["application/octet-stream"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema { Type = "string", Format = "binary" }
        };
        response.Headers["X-Content-SHA256"] = new OpenApiHeader
        {
            Description = "Lowercase SHA-256 digest of the exact immutable material file bytes returned.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Pattern = "^[0-9a-f]{64}$"
            }
        };
        response.Headers["X-Content-Type-Options"] = new OpenApiHeader
        {
            Description = "Always nosniff; material bytes must not be interpreted using a different content type.",
            Schema = new OpenApiSchema { Type = "string" }
        };
        response.Headers["Cache-Control"] = new OpenApiHeader
        {
            Description = "Authenticated owner material is private and must not be stored by shared caches.",
            Schema = new OpenApiSchema { Type = "string" }
        };
        response.Headers["Content-Disposition"] = new OpenApiHeader
        {
            Description = "Attachment filename derived from the immutable material path.",
            Schema = new OpenApiSchema { Type = "string" }
        };
    }
}
