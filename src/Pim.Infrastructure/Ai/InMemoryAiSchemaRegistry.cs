using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai;

public sealed class InMemoryAiSchemaRegistry : IAiSchemaRegistry
{
    private readonly Dictionary<(string Name, string Version), AiSchemaDefinition> _schemas = [];

    public void Register(AiSchemaDefinition schema)
        => _schemas[(schema.Name, schema.Version)] = schema;

    public AiSchemaDefinition? Find(string name, string version)
        => _schemas.TryGetValue((name, version), out var schema) ? schema : null;
}
