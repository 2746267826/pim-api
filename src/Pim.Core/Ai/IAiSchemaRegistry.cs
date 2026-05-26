namespace Pim.Core.Ai;

public interface IAiSchemaRegistry
{
    void Register(AiSchemaDefinition schema);
    AiSchemaDefinition? Get(string name, string version);
}
