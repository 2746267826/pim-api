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
}
