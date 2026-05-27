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
            var schema = ParseSchema(schemaJson);
            var results = EvaluateSchema(schema, document.RootElement);

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
        catch (JsonException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid JSON: {ex.Message}"]);
        }
        catch (InvalidSchemaException ex)
        {
            return new AiSchemaValidationResult(false, null, [$"Invalid schema: {ex.Message}"]);
        }
    }

    private static JsonSchema ParseSchema(string schemaJson)
    {
        try
        {
            return JsonSchema.FromText(schemaJson);
        }
        catch (JsonSchemaException ex)
        {
            throw new InvalidSchemaException(ex.Message, ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidSchemaException(ex.Message, ex);
        }
    }

    private static EvaluationResults EvaluateSchema(JsonSchema schema, JsonElement response)
    {
        try
        {
            return schema.Evaluate(
                response,
                new EvaluationOptions { OutputFormat = OutputFormat.List });
        }
        catch (JsonSchemaException ex)
        {
            throw new InvalidSchemaException(ex.Message, ex);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidSchemaException(ex.Message, ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidSchemaException(ex.Message, ex);
        }
    }

    private static IEnumerable<string> CollectErrors(EvaluationResults results)
    {
        if (results.Errors is not null)
        {
            foreach (var error in results.Errors)
            {
                yield return $"{FormatInstanceLocation(results)}: {error.Key} {error.Value}";
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

    private static string FormatInstanceLocation(EvaluationResults results)
    {
        var location = results.InstanceLocation.ToString();
        return string.IsNullOrWhiteSpace(location) ? "$" : location;
    }

    private sealed class InvalidSchemaException(string message, Exception innerException)
        : Exception(message, innerException);
}
