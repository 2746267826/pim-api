using System.Security.Cryptography;
using System.Text;

namespace Pim.Module.Files.Services;

public sealed record FileTextChunk(int ChunkIndex, string Text, string TextHash, int StartOffset, int EndOffset);

public static class FileChunker
{
    private const int DefaultMaxChars = 1600;
    private const int DefaultOverlapChars = 160;

    public static IReadOnlyList<FileTextChunk> Chunk(
        string? text,
        int maxChars = DefaultMaxChars,
        int overlapChars = DefaultOverlapChars)
    {
        if (maxChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxChars), "Maximum chunk length must be greater than zero.");
        if (overlapChars < 0)
            throw new ArgumentOutOfRangeException(nameof(overlapChars), "Overlap length cannot be negative.");

        if (string.IsNullOrEmpty(text) || text.All(IsSkippable))
            return [];

        var effectiveOverlap = Math.Min(overlapChars, maxChars - 1);
        var chunks = new List<FileTextChunk>();
        var start = FirstContentOffset(text, 0);

        while (start < text.Length)
        {
            var end = FindChunkEnd(text, start, maxChars);
            var trimmedStart = FirstContentOffset(text, start);
            var trimmedEnd = LastContentOffset(text, end);

            if (trimmedStart < trimmedEnd)
            {
                var chunkText = text[trimmedStart..trimmedEnd];
                chunks.Add(new FileTextChunk(
                    chunks.Count,
                    chunkText,
                    Sha256LowerHex(chunkText),
                    trimmedStart,
                    trimmedEnd));
            }

            if (end >= text.Length)
                break;

            start = Math.Max(end - effectiveOverlap, start + 1);
            start = FirstContentOffset(text, start);
        }

        return chunks;
    }

    private static int FindChunkEnd(string text, int start, int maxChars)
    {
        var hardEnd = Math.Min(start + maxChars, text.Length);
        if (hardEnd >= text.Length)
            return text.Length;

        for (var index = hardEnd; index > start; index--)
        {
            if (char.IsWhiteSpace(text[index - 1]))
                return index - 1;
        }

        return hardEnd;
    }

    private static int FirstContentOffset(string text, int start)
    {
        var index = Math.Clamp(start, 0, text.Length);
        while (index < text.Length && IsSkippable(text[index]))
            index++;

        return index;
    }

    private static int LastContentOffset(string text, int end)
    {
        var index = Math.Clamp(end, 0, text.Length);
        while (index > 0 && IsSkippable(text[index - 1]))
            index--;

        return index;
    }

    private static bool IsSkippable(char value)
        => char.IsWhiteSpace(value) || char.IsControl(value);

    private static string Sha256LowerHex(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
