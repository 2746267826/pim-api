using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace Pim.Module.Files.Services;

public sealed record FileChunkVector(
    string PointId,
    Guid UserId,
    Guid ProviderId,
    Guid FileItemId,
    Guid VersionId,
    Guid ChunkId,
    string Path,
    string? MimeType,
    DateTimeOffset ModifiedAt,
    float[] Vector);

public sealed record FileChunkSearchHit(Guid ChunkId, Guid FileItemId, Guid VersionId, decimal Score);

public interface IFileVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct = default);
    Task UpsertChunksAsync(IReadOnlyList<FileChunkVector> vectors, CancellationToken ct = default);
    Task DeleteFileVectorsAsync(Guid fileItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FileChunkSearchHit>> SearchAsync(float[] vector, Guid userId, string? mode, CancellationToken ct = default);
}

public sealed class QdrantFileVectorStore(
    HttpClient httpClient,
    IConfiguration configuration,
    IFileEmbeddingService embeddingService) : IFileVectorStore
{
    private readonly Uri _baseUri = new((configuration["Qdrant:BaseUrl"] ?? "http://qdrant:6333").TrimEnd('/') + "/");
    private readonly string _collection = configuration["Qdrant:Collection"] ?? "file_chunks";

    public async Task EnsureCollectionAsync(CancellationToken ct = default)
    {
        await SendAsync(
            HttpMethod.Put,
            CollectionPath(),
            new
            {
                vectors = new
                {
                    size = embeddingService.Dimensions,
                    distance = "Cosine"
                }
            },
            ct,
            HttpStatusCode.Conflict);
    }

    public async Task UpsertChunksAsync(IReadOnlyList<FileChunkVector> vectors, CancellationToken ct = default)
    {
        if (vectors.Count == 0)
            return;

        await SendAsync(
            HttpMethod.Put,
            $"{CollectionPath()}/points",
            new
            {
                points = vectors.Select(vector => new
                {
                    id = vector.PointId,
                    vector = vector.Vector,
                    payload = new
                    {
                        userId = vector.UserId.ToString(),
                        providerId = vector.ProviderId.ToString(),
                        fileId = vector.FileItemId.ToString(),
                        versionId = vector.VersionId.ToString(),
                        chunkId = vector.ChunkId.ToString(),
                        path = vector.Path,
                        mimeType = vector.MimeType,
                        modifiedAt = vector.ModifiedAt
                    }
                }).ToArray()
            },
            ct);
    }

    public Task DeleteFileVectorsAsync(Guid fileItemId, CancellationToken ct = default)
        => SendAsync(
            HttpMethod.Post,
            $"{CollectionPath()}/points/delete",
            new
            {
                filter = new
                {
                    must = new[]
                    {
                        Match("fileId", fileItemId.ToString())
                    }
                }
            },
            ct);

    public async Task<IReadOnlyList<FileChunkSearchHit>> SearchAsync(
        float[] vector,
        Guid userId,
        string? mode,
        CancellationToken ct = default)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            $"{CollectionPath()}/points/search",
            new
            {
                vector,
                limit = 20,
                with_payload = true,
                filter = new
                {
                    must = new[]
                    {
                        Match("userId", userId.ToString())
                    }
                }
            },
            ct);
        var result = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>(cancellationToken: ct);

        return result?.Result?
            .Select(hit => new FileChunkSearchHit(
                Guid.Parse(hit.Payload.ChunkId),
                Guid.Parse(hit.Payload.FileId),
                Guid.Parse(hit.Payload.VersionId),
                hit.Score))
            .ToList()
            ?? [];
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object body,
        CancellationToken ct,
        params HttpStatusCode[] acceptedStatusCodes)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        request.RequestUri = new Uri(_baseUri, path.TrimStart('/'));
        var response = await httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && !acceptedStatusCodes.Contains(response.StatusCode))
            response.EnsureSuccessStatusCode();

        return response;
    }

    private string CollectionPath()
        => $"/collections/{Uri.EscapeDataString(_collection)}";

    private static object Match(string key, string value)
        => new
        {
            key,
            match = new
            {
                value
            }
        };

    private sealed record QdrantSearchResponse(IReadOnlyList<QdrantSearchPoint>? Result);

    private sealed record QdrantSearchPoint(
        decimal Score,
        QdrantSearchPayload Payload);

    private sealed record QdrantSearchPayload(
        [property: JsonPropertyName("fileId")] string FileId,
        [property: JsonPropertyName("versionId")] string VersionId,
        [property: JsonPropertyName("chunkId")] string ChunkId);
}
