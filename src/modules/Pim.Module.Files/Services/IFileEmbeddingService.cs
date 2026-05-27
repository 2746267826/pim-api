namespace Pim.Module.Files.Services;

public interface IFileEmbeddingService
{
    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
}
