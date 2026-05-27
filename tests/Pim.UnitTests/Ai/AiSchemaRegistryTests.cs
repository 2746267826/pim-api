using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiSchemaRegistryTests
{
    [Fact]
    public void Get_ReturnsRegisteredSchemaByNameAndVersion()
    {
        var registry = new AiSchemaRegistry();
        var schema = new AiSchemaDefinition(
            Name: "quick-note-conversion",
            Version: "1",
            JsonSchema: """{"type":"object"}""",
            Description: "Converts a quick note into structured data.");

        registry.Register(schema);

        var found = registry.Get("quick-note-conversion", "1");

        Assert.Equal(schema, found);
    }
}
