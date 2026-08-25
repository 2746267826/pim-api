using System.Security.Cryptography;
using System.Text;

namespace Pim.Module.Files.Services;

/// <summary>
/// Development stub using hashing trick - NOT semantic. Production should replace with real model (e.g. BGE / text-embedding-3-small).
/// Improved to use signed multi-hash to reduce collisions and better spread.
/// </summary>
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
            // Spread each token across 2 hashed dimensions with signed weights to reduce collisions
            var (dim1, sign1) = HashToDimensionAndSign(token, 0);
            var (dim2, sign2) = HashToDimensionAndSign(token, 1);
            vector[dim1] += sign1 * increment * 0.7f;
            vector[dim2] += sign2 * increment * 0.3f;
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

    private (int dim, float sign) HashToDimensionAndSign(string token, int seed)
    {
        Span<byte> hash = stackalloc byte[32];
        var input = seed == 0 ? token : $"{token}\0{seed}";
        SHA256.HashData(Encoding.UTF8.GetBytes(input), hash);
        var dim = (int)(BitConverter.ToUInt32(hash[..4]) % (uint)Dimensions);
        var sign = (hash[4] & 1) == 0 ? 1f : -1f;
        return (dim, sign);
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
