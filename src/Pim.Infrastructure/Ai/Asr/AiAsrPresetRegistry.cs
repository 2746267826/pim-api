using Pim.Core.Ai;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class AiAsrPresetRegistry
{
    private readonly IReadOnlyDictionary<string, IAiAsrPreset> _presets;
    public AiAsrPresetRegistry(IEnumerable<IAiAsrPreset> presets) => _presets = presets.ToDictionary(x => x.Descriptor.Id, StringComparer.Ordinal);
    public IReadOnlyList<AiAsrPresetDto> List() => _presets.Values.Select(x => x.Descriptor).OrderBy(x => x.Id).ToArray();
    public IAiAsrPreset Resolve(string? id) => id is not null && _presets.TryGetValue(id, out var preset) ? preset
        : throw new DomainException(40001, "ASR预设未选择或不可用，请编辑模型配置");
    public static string OperationEndpoint(string root, AiAsrPresetDto preset) => new Uri(new Uri(root), preset.OperationPath).AbsoluteUri;
}
