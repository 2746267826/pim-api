using System.Text.RegularExpressions;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// Native-note package downloads are range-capable ZIP streams with integrity and binding
/// headers. Minimal API metadata cannot express 206/416 or custom headers, so document exactly
/// what the endpoint returns for generated clients.
/// </summary>
public sealed class NoteMcpPackageContentOperationFilter : IOperationFilter
{
    private static readonly Regex PackagePath = new(
        @"^/?api/v1/native-notes/mcp/notes/\{noteId(?::guid)?\}/package/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
            description.RelativePath is null ||
            !PackagePath.IsMatch(description.RelativePath))
            return;

        operation.Responses.TryAdd("200", new OpenApiResponse { Description = "Success" });
        operation.Responses.TryAdd("206", new OpenApiResponse
        {
            Description = "Partial content for a valid single byte range.",
        });
        operation.Responses.TryAdd("416", new OpenApiResponse
        {
            Description = "Requested range is not satisfiable.",
        });

        DocumentZipResponse(operation.Responses["200"]);
        var partial = operation.Responses["206"];
        DocumentZipResponse(partial);
        partial.Headers["Content-Range"] = RangeHeader(
            "Satisfied range, for example bytes 0-1048575/104857600.");

        var unsatisfiable = operation.Responses["416"];
        DocumentZipHeaders(unsatisfiable, includeContent: false);
        unsatisfiable.Headers["Content-Range"] =
            RangeHeader("Unsatisfied range, for example bytes */104857600.");
    }

    private static void DocumentZipResponse(OpenApiResponse response)
    {
        response.Content["application/zip"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema { Type = "string", Format = "binary" },
        };
        DocumentZipHeaders(response, includeContent: true);
    }

    private static void DocumentZipHeaders(OpenApiResponse response, bool includeContent)
    {
        if (!includeContent) response.Content.Clear();
        response.Headers["Accept-Ranges"] = new OpenApiHeader
        {
            Description = "Always bytes; clients may request a single byte range.",
            Schema = new OpenApiSchema { Type = "string" },
        };
        response.Headers["Content-Disposition"] = new OpenApiHeader
        {
            Description = "Attachment filename in the form {noteId}.pimnote.zip.",
            Schema = new OpenApiSchema { Type = "string" },
        };
        response.Headers["Cache-Control"] = new OpenApiHeader
        {
            Description = "Always private, no-store.",
            Schema = new OpenApiSchema { Type = "string" },
        };
        response.Headers["X-PIM-Note-Id"] = new OpenApiHeader
        {
            Description = "Note id bound to the generated package.",
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
        };
        response.Headers["X-PIM-Version-Id"] = new OpenApiHeader
        {
            Description = "Material version id bound to the generated package.",
            Schema = new OpenApiSchema { Type = "string", Format = "uuid" },
        };
        response.Headers["X-PIM-Package-Sha256"] = new OpenApiHeader
        {
            Description = "Lowercase SHA-256 of the complete package, not only the returned range.",
            Schema = new OpenApiSchema
            {
                Type = "string",
                Pattern = "^[0-9a-f]{64}$",
            },
        };
        response.Headers["X-PIM-Package-Byte-Length"] = new OpenApiHeader
        {
            Description = "Complete package byte length, not only the returned range.",
            Schema = new OpenApiSchema { Type = "integer", Format = "int64" },
        };
    }

    private static OpenApiHeader RangeHeader(string description) => new()
    {
        Description = description,
        Schema = new OpenApiSchema { Type = "string" },
    };
}
