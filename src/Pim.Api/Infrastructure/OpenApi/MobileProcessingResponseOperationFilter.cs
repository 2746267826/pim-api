using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi.Models;
using Pim.Module.Mobile;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// Declares the Retry-After header on the 202 response of endpoints marked with
/// <see cref="MobileUsageProcessingResponseAttribute"/>; minimal API metadata cannot
/// express response headers through <c>Produces</c>.
/// </summary>
public sealed class MobileProcessingResponseOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var marked = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<MobileUsageProcessingResponseAttribute>()
            .Any();
        if (!marked)
            return;

        if (operation.Responses.TryGetValue("202", out var response) && response is not null)
        {
            response.Headers["Retry-After"] = new OpenApiHeader
            {
                Description = "Seconds until the earliest retry (not-before hint), matching "
                    + "the retryAfterSeconds field of the 202 envelope. Minimum 1.",
                Schema = new OpenApiSchema
                {
                    Type = "integer",
                    Format = "int32",
                    Minimum = 1
                }
            };
        }
    }
}
