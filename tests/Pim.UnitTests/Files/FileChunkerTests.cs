using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileChunkerTests
{
    [Fact]
    public void Chunk_SplitsTextWithOffsetsOverlapAndStableHashes()
    {
        var chunks = FileChunker.Chunk("alpha beta gamma delta epsilon", maxChars: 12, overlapChars: 3);

        Assert.True(chunks.Count >= 3);
        Assert.Equal(0, chunks[0].ChunkIndex);
        Assert.Equal(0, chunks[0].StartOffset);
        Assert.True(chunks[0].EndOffset > chunks[0].StartOffset);
        Assert.Equal(64, chunks[0].TextHash.Length);
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            Assert.False(string.IsNullOrWhiteSpace(chunk.Text));
            Assert.Equal(chunk.Text, "alpha beta gamma delta epsilon"[chunk.StartOffset..chunk.EndOffset]);
            Assert.Equal(index, chunk.ChunkIndex);
        }

        Assert.Contains(chunks.Zip(chunks.Skip(1)), pair =>
            pair.First.EndOffset > pair.Second.StartOffset);
        Assert.All(chunks, chunk => Assert.Equal(chunk.TextHash.ToLowerInvariant(), chunk.TextHash));
    }

    [Fact]
    public void Chunk_PrefersWhitespaceBeforeHardLimit()
    {
        var chunks = FileChunker.Chunk("alpha beta gamma", maxChars: 11, overlapChars: 0);

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("alpha beta", chunk.Text),
            chunk => Assert.Equal("gamma", chunk.Text));
    }

    [Fact]
    public void Chunk_SplitsAtHardLimitWhenNoWhitespaceExists()
    {
        var chunks = FileChunker.Chunk("abcdefghijkl", maxChars: 5, overlapChars: 0);

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("abcde", chunk.Text),
            chunk => Assert.Equal("fghij", chunk.Text),
            chunk => Assert.Equal("kl", chunk.Text));
    }

    [Fact]
    public void Chunk_ReturnsEmptyForControlOrWhitespaceOnlyText()
    {
        var chunks = FileChunker.Chunk("\r\n\t\u0000\u0001", maxChars: 8, overlapChars: 2);

        Assert.Empty(chunks);
    }
}

public class HashingFileEmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_ReturnsDeterministicNormalized384DimensionalVector()
    {
        IFileEmbeddingService service = new HashingFileEmbeddingService();

        var first = await service.EmbedAsync("Alpha, beta! ALPHA");
        var second = await service.EmbedAsync("alpha beta alpha");

        Assert.Equal(384, service.Dimensions);
        Assert.Equal(384, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(1f, L2Norm(first), precision: 5);
    }

    [Fact]
    public async Task EmbedAsync_ReturnsZeroVectorWhenTextHasNoTokens()
    {
        var service = new HashingFileEmbeddingService();

        var vector = await service.EmbedAsync(" , . \r\n\t");

        Assert.Equal(384, vector.Length);
        Assert.All(vector, value => Assert.Equal(0f, value));
    }

    private static float L2Norm(float[] vector)
        => MathF.Sqrt(vector.Sum(value => value * value));
}
