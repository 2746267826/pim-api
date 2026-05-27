using System.Collections.Concurrent;
using Pim.Core.Ai;

namespace Pim.Infrastructure.Ai;

public sealed class AiSchemaRegistry : IAiSchemaRegistry
{
    private readonly ConcurrentDictionary<(string Name, string Version), AiSchemaDefinition> _schemas = new();

    public void Register(AiSchemaDefinition schema)
    {
        _schemas[(schema.Name, schema.Version)] = schema;
    }

    public AiSchemaDefinition? Get(string name, string version)
    {
        return _schemas.TryGetValue((name, version), out var schema) ? schema : null;
    }
}
