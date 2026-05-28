using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class QdrantFileVectorStoreTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ProviderId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FileItemId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid VersionId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
    private static readonly Guid ChunkId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");

    [Fact]
    public async Task EnsureCollectionAsync_PutsCollectionWithEmbeddingDimensions()
    {
        var handler = new CapturingHandler(OkBody());
        var store = CreateStore(handler, dimensions: 3);

        await store.EnsureCollectionAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://qdrant.test/collections/file_chunks", request.Url);

        using var document = JsonDocument.Parse(request.Body);
        var vectors = document.RootElement.GetProperty("vectors");
        Assert.Equal(3, vectors.GetProperty("size").GetInt32());
        Assert.Equal("Cosine", vectors.GetProperty("distance").GetString());
    }

    [Fact]
    public async Task EnsureCollectionAsync_WhenCollectionAlreadyExistsTreatsConflictAsReady()
    {
        var handler = new CapturingHandler("""{"status":"error","result":null}""", HttpStatusCode.Conflict);
        var store = CreateStore(handler);

        await store.EnsureCollectionAsync();

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://qdrant.test/collections/file_chunks", request.Url);
    }

    [Fact]
    public async Task UpsertChunksAsync_SendsVectorPointPayload()
    {
        var handler = new CapturingHandler(OkBody());
        var store = CreateStore(handler);
        var modifiedAt = DateTimeOffset.Parse("2026-05-27T10:30:00+00:00");

        await store.UpsertChunksAsync([
            new FileChunkVector(
                "point-1",
                UserId,
                ProviderId,
                FileItemId,
                VersionId,
                ChunkId,
                "/Reports/report.docx",
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                modifiedAt,
                [0.25f, 0.5f])
        ]);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("http://qdrant.test/collections/file_chunks/points", request.Url);

        using var document = JsonDocument.Parse(request.Body);
        var point = document.RootElement.GetProperty("points")[0];
        Assert.Equal("point-1", point.GetProperty("id").GetString());
        Assert.Equal(0.25f, point.GetProperty("vector")[0].GetSingle());
        Assert.Equal(0.5f, point.GetProperty("vector")[1].GetSingle());

        var payload = point.GetProperty("payload");
        Assert.Equal(UserId.ToString(), payload.GetProperty("userId").GetString());
        Assert.Equal(ProviderId.ToString(), payload.GetProperty("providerId").GetString());
        Assert.Equal(FileItemId.ToString(), payload.GetProperty("fileId").GetString());
        Assert.Equal(VersionId.ToString(), payload.GetProperty("versionId").GetString());
        Assert.Equal(ChunkId.ToString(), payload.GetProperty("chunkId").GetString());
        Assert.Equal("/Reports/report.docx", payload.GetProperty("path").GetString());
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", payload.GetProperty("mimeType").GetString());
        Assert.Equal(modifiedAt, payload.GetProperty("modifiedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task DeleteFileVectorsAsync_DeletesByFileIdFilter()
    {
        var handler = new CapturingHandler(OkBody());
        var store = CreateStore(handler);

        await store.DeleteFileVectorsAsync(FileItemId);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://qdrant.test/collections/file_chunks/points/delete", request.Url);

        using var document = JsonDocument.Parse(request.Body);
        var condition = Assert.Single(document.RootElement.GetProperty("filter").GetProperty("must").EnumerateArray());
        Assert.Equal("fileId", condition.GetProperty("key").GetString());
        Assert.Equal(FileItemId.ToString(), condition.GetProperty("match").GetProperty("value").GetString());
    }

    [Fact]
    public async Task SearchAsync_FiltersByUserIdAndMapsHits()
    {
        var handler = new CapturingHandler("""
            {
              "result": [
                {
                  "id": "point-1",
                  "score": 0.92,
                  "payload": {
                    "userId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                    "providerId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
                    "fileId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
                    "versionId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
                    "chunkId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
                    "path": "/Reports/report.docx",
                    "mimeType": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    "modifiedAt": "2026-05-27T10:30:00+00:00"
                  }
                }
              ],
              "status": "ok"
            }
            """);
        var store = CreateStore(handler);

        var hits = await store.SearchAsync([0.25f, 0.5f], UserId, "semantic");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("http://qdrant.test/collections/file_chunks/points/search", request.Url);

        using var document = JsonDocument.Parse(request.Body);
        Assert.Equal(0.25f, document.RootElement.GetProperty("vector")[0].GetSingle());
        Assert.True(document.RootElement.GetProperty("with_payload").GetBoolean());
        var condition = Assert.Single(document.RootElement.GetProperty("filter").GetProperty("must").EnumerateArray());
        Assert.Equal("userId", condition.GetProperty("key").GetString());
        Assert.Equal(UserId.ToString(), condition.GetProperty("match").GetProperty("value").GetString());

        var hit = Assert.Single(hits);
        Assert.Equal(ChunkId, hit.ChunkId);
        Assert.Equal(FileItemId, hit.FileItemId);
        Assert.Equal(VersionId, hit.VersionId);
        Assert.Equal(0.92m, hit.Score);
    }

    private static QdrantFileVectorStore CreateStore(CapturingHandler handler, int dimensions = 384)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qdrant:BaseUrl"] = "http://qdrant.test",
                ["Qdrant:Collection"] = "file_chunks"
            })
            .Build();

        return new QdrantFileVectorStore(new HttpClient(handler), configuration, new FakeEmbeddingService(dimensions));
    }

    private static string OkBody()
        => """{"result": true, "status": "ok"}""";

    private sealed class FakeEmbeddingService : IFileEmbeddingService
    {
        public FakeEmbeddingService(int dimensions)
        {
            Dimensions = dimensions;
        }

        public int Dimensions { get; }

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new float[Dimensions]);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                body));

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed record CapturedRequest(string Method, string Url, string Body);
}
