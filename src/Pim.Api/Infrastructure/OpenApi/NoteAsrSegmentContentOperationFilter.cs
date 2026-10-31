using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// Segment input is a bounded WAV slice, not a JSON envelope. Its lease identity is supplied
/// through headers (not a replayable query string) and every immutable binding fact is exported.
/// </summary>
public sealed class NoteAsrSegmentContentOperationFilter : IOperationFilter
{
    private const string RelativePath = "api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(description.RelativePath?.TrimStart('/'), RelativePath, StringComparison.Ordinal))
            return;

        operation.Responses.TryAdd("200", new OpenApiResponse { Description = "Success" });
        var response = operation.Responses["200"];
        response.Content["audio/wav"] = new OpenApiMediaType
        {
            Schema = new OpenApiSchema { Type = "string", Format = "binary" }
        };
        foreach (var name in new[]
                 {
                     "X-Content-SHA256", "X-Pim-Source-SHA256", "X-Pim-Owner-Id", "X-Pim-Node-Id",
                     "X-Pim-Lease-Id", "X-Pim-Attempt-Id", "X-Pim-Version-Id", "X-Pim-Fencing-Token",
                     "X-Pim-Segment-Index", "X-Pim-Source-File-Index", "X-Pim-Source-Offset-Samples",
                     "X-Pim-Samples"
                 })
        {
            response.Headers[name] = new OpenApiHeader
            {
                Description = name.Contains("SHA256", StringComparison.Ordinal)
                    ? "SHA-256 digest of the returned slice or the immutable source file."
                    : "Immutable routing identity/binding metadata for the returned segment.",
                Schema = new OpenApiSchema
                {
                    Type = name is "X-Pim-Fencing-Token" or "X-Pim-Segment-Index" or
                        "X-Pim-Source-File-Index" or "X-Pim-Source-Offset-Samples" or "X-Pim-Samples"
                        ? "integer" : "string",
                    Format = name is "X-Pim-Fencing-Token" or "X-Pim-Source-Offset-Samples" or "X-Pim-Samples"
                        ? "int64" : name is "X-Pim-Segment-Index" or "X-Pim-Source-File-Index" ? "int32" : null
                }
            };
        }
    }
}
