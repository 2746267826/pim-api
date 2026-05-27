using System.Text.Json;
using Json.Schema;

namespace Pim.Infrastructure.Ai;

public sealed record AiSchemaValidationResult(
    bool IsValid,
    string? ParsedOutputJson,
    IReadOnlyList<string> Errors);

public static class AiSchemaValidator
{
    public static AiSchemaValidationResult Validate(string responseText, string schemaJson)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(responseText);
        }
        catch (JsonException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid JSON: {ex.Message}"]);
        }

        JsonSchema schema;

        try
        {
            schema = JsonSchema.FromText(schemaJson);
        }
        catch (JsonSchemaException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid schema: {ex.Message}"]);
        }
        catch (JsonException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid schema: {ex.Message}"]);
        }

        using (document)
        {
            var results = schema.Evaluate(
                document.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            if (results.IsValid)
            {
                return new AiSchemaValidationResult(true, JsonSerializer.Serialize(document.RootElement), []);
            }

            var errors = CollectErrors(results)
                .Distinct()
                .ToArray();

            return new AiSchemaValidationResult(
                false,
                null,
                errors.Length == 0 ? ["JSON did not match schema."] : errors);
        }
    }

    private static IEnumerable<string> CollectErrors(EvaluationResults results)
    {
        if (results.Errors is not null)
        {
            foreach (var error in results.Errors)
            {
                yield return $"{results.InstanceLocation}: {error.Key} {error.Value}";
            }
        }

        if (results.Details is null)
        {
            yield break;
        }

        foreach (var detail in results.Details)
        {
            foreach (var error in CollectErrors(detail))
            {
                yield return error;
            }
        }
    }
}
