using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Pim.Api.Infrastructure.OpenApi;

/// <summary>
/// The endpoint binds a multipart form through an [FromForm] parameter. Swashbuckle otherwise
/// documents one nested "form" field. Flatten the typed form itself onto the multipart root so
/// generated clients receive the actual field names and an uploadable binary file part.
/// </summary>
public sealed class NoteAsrBatchFormOperationFilter : IOperationFilter
{
    private const string BatchPath = "api/v1/native-notes/asr-routing/batches";
    private const string Multipart = "multipart/form-data";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var description = context.ApiDescription;
        if (!string.Equals(description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(description.RelativePath?.TrimStart('/').TrimEnd('/'), BatchPath, StringComparison.Ordinal))
            return;

        var form = context.SchemaGenerator.GenerateSchema(
            typeof(Pim.Api.Endpoints.StartNoteAsrBatchForm), context.SchemaRepository);
        var fieldsSource = form;
        if (form.Reference is { } reference)
        {
            if (!context.SchemaRepository.Schemas.TryGetValue(reference.Id, out fieldsSource) ||
                fieldsSource.Properties.Count == 0)
                throw new InvalidOperationException(
                    $"Batch form schema '{reference.Id}' is not registered in the schema repository.");
        }
        if (fieldsSource.Type != "object" || fieldsSource.Properties.Count == 0)
            throw new InvalidOperationException("Batch form schema has no typed properties to flatten.");

        var fields = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>(StringComparer.Ordinal),
            Required = new HashSet<string>(StringComparer.Ordinal)
        };
        var encoding = new Dictionary<string, OpenApiEncoding>(StringComparer.Ordinal);
        foreach (var (name, property) in fieldsSource.Properties)
        {
            // Swashbuckle describes IFormFile as an OpenAPI 2-style "file" string. OpenAPI 3
            // multipart uploads use a string with the binary format.
            fields.Properties[name] = string.Equals(property.Type, "file", StringComparison.OrdinalIgnoreCase)
                ? new OpenApiSchema { Type = "string", Format = "binary" }
                : property;
            if (fieldsSource.Required?.Contains(name) == true)
                fields.Required.Add(name);
            encoding[name] = new OpenApiEncoding { Style = ParameterStyle.Form };
        }
        if (fieldsSource.AdditionalPropertiesAllowed is false)
            fields.AdditionalPropertiesAllowed = false;
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content =
            {
                [Multipart] = new OpenApiMediaType { Schema = fields, Encoding = encoding }
            }
        };
    }
}
