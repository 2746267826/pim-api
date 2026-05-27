using System.Security.Cryptography;
using System.Text;

namespace Pim.Module.Files.Services;

public sealed class HashingFileEmbeddingService : IFileEmbeddingService
{
    public const int DefaultDimensions = 384;

    public int Dimensions { get; }

    public HashingFileEmbeddingService(int dimensions = DefaultDimensions)
    {
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Embedding dimensions must be greater than zero.");

        Dimensions = dimensions;
    }

    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var tokens = Tokenize(text).ToList();
        var vector = new float[Dimensions];
        if (tokens.Count == 0)
            return Task.FromResult(vector);

        var increment = 1f / MathF.Sqrt(tokens.Count);
        foreach (var token in tokens)
        {
            ct.ThrowIfCancellationRequested();
            var dimension = HashToDimension(token);
            vector[dimension] += increment;
        }

        Normalize(vector);
        return Task.FromResult(vector);
    }

    private static IEnumerable<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                builder.Append(Rune.ToLowerInvariant(rune));
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
            yield return builder.ToString();
    }

    private int HashToDimension(string token)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(token), hash);
        var value = BitConverter.ToUInt32(hash[..4]);

        return (int)(value % (uint)Dimensions);
    }

    private static void Normalize(float[] vector)
    {
        var magnitudeSquared = 0f;
        foreach (var value in vector)
            magnitudeSquared += value * value;

        if (magnitudeSquared == 0f)
            return;

        var magnitude = MathF.Sqrt(magnitudeSquared);
        for (var index = 0; index < vector.Length; index++)
            vector[index] /= magnitude;
    }
}
