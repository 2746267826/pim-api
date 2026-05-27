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
        try
        {
            using var document = JsonDocument.Parse(responseText);
            var schema = JsonSchema.FromText(schemaJson);
            var results = schema.Evaluate(
                document.RootElement,
                new EvaluationOptions { OutputFormat = OutputFormat.List });

            if (results.IsValid)
            {
                return new AiSchemaValidationResult(true, document.RootElement.GetRawText(), []);
            }

            var errors = CollectErrors(results)
                .Distinct()
                .ToArray();

            return new AiSchemaValidationResult(
                false,
                null,
                errors.Length == 0 ? ["JSON did not match schema."] : errors);
        }
        catch (JsonException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid JSON: {ex.Message}"]);
        }
        catch (JsonSchemaException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid schema: {ex.Message}"]);
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
