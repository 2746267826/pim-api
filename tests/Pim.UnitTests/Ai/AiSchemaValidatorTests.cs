using Pim.Infrastructure.Ai;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiSchemaValidatorTests
{
    private const string SchemaJson = """
        {
          "type": "object",
          "required": ["title"],
          "properties": {
            "title": { "type": "string" }
          },
          "additionalProperties": false
        }
        """;

    [Fact]
    public void Validate_ValidOutput_ReturnsParsedCompactJson()
    {
        const string responseText = """{"title":"Inbox"}""";

        var result = AiSchemaValidator.Validate(responseText, SchemaJson);

        Assert.True(result.IsValid);
        Assert.Equal(responseText, result.ParsedOutputJson);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidOutput_ReturnsSchemaErrors()
    {
        var result = AiSchemaValidator.Validate("""{"name":"Inbox"}""", SchemaJson);

        Assert.False(result.IsValid);
        Assert.Null(result.ParsedOutputJson);
        Assert.Contains(result.Errors, error => error.Contains("title"));
    }

    [Fact]
    public void Validate_FormattedValidOutput_ReturnsCompactParsedJson()
    {
        const string responseText = """
            {
              "title": "Inbox"
            }
            """;

        var result = AiSchemaValidator.Validate(responseText, SchemaJson);

        Assert.True(result.IsValid);
        Assert.Equal("""{"title":"Inbox"}""", result.ParsedOutputJson);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Validate_InvalidResponseJson_ReturnsInvalidJsonError()
    {
        var result = AiSchemaValidator.Validate("""{"title":""", SchemaJson);

        Assert.False(result.IsValid);
        Assert.Null(result.ParsedOutputJson);
        Assert.Contains(result.Errors, error => error.StartsWith("Invalid JSON:"));
    }

    [Fact]
    public void Validate_InvalidSchemaJson_ReturnsInvalidSchemaError()
    {
        var result = AiSchemaValidator.Validate("""{"title":"Inbox"}""", """{"type":""");

        Assert.False(result.IsValid);
        Assert.Null(result.ParsedOutputJson);
        Assert.Contains(result.Errors, error => error.StartsWith("Invalid schema:"));
    }

    [Fact]
    public void Validate_UnresolvableSchemaReference_ReturnsInvalidSchemaError()
    {
        var result = AiSchemaValidator.Validate("""{"title":"Inbox"}""", """{"$ref":"#/definitions/missing"}""");

        Assert.False(result.IsValid);
        Assert.Null(result.ParsedOutputJson);
        Assert.Contains(result.Errors, error => error.StartsWith("Invalid schema:"));
    }
}
