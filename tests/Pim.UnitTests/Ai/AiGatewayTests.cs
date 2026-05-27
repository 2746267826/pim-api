using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Ai;

public class AiGatewayTests
{
    [Fact]
    public async Task CompleteAsync_ReturnsBlockedAndDoesNotCallProvider_WhenAiDisabled()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient("""{"title":"Inbox"}""");
        var gateway = CreateGateway(db, fakeClient, enabled: false);

        var result = await gateway.CompleteAsync(BasicRequest());

        Assert.Equal(AiRequestStatus.Blocked, result.Status);
        Assert.Equal(0, fakeClient.CallCount);
        Assert.Contains("AI is disabled", result.UserFacingError);
        Assert.Equal("blocked", (await db.AiRequestLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompleteAsync_LogsSuccessWithTokenUsage()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient("plain answer", promptTokens: 4, completionTokens: 6);
        var gateway = CreateGateway(db, fakeClient, enabled: true);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null));

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("plain answer", result.ResponseText);
        Assert.Equal(10, result.Usage.TotalTokens);
        Assert.Equal("succeeded", (await db.AiRequestLogs.SingleAsync()).Status);
    }

    [Fact]
    public async Task CompleteAsync_RetriesValidationOnceWithoutExpandingOriginalContext()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient(["""{"name":"Inbox"}""", """{"title":"Inbox"}"""]);
        var registry = new AiSchemaRegistry();
        registry.Register(new AiSchemaDefinition(
            "quick-note-conversion",
            "1",
            """{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}""",
            "Quick note conversion"));
        var gateway = CreateGateway(db, fakeClient, enabled: true, registry: registry);

        var result = await gateway.CompleteAsync(BasicRequest(maxAttempts: 2));

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("""{"title":"Inbox"}""", result.ParsedOutputJson);
        Assert.Equal(2, fakeClient.CallCount);
        Assert.Contains(fakeClient.Requests[1], message => message.Text?.Contains("Fix only the JSON", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(fakeClient.Requests[1], message => message.Text == "convert this note");
        Assert.Equal(2, await db.AiRequestLogs.CountAsync());
    }

    private static AiGatewayRequest BasicRequest(string? schemaName = "quick-note-conversion", string? schemaVersion = "1", int maxAttempts = 1)
        => new(
            Module: "quick-notes",
            Purpose: "quick-notes.convert",
            SourceObjectType: "quick_note",
            SourceObjectId: "note-1",
            Messages: [new AiMessage(AiMessageRole.User, "convert this note")],
            Model: null,
            SchemaName: schemaName,
            SchemaVersion: schemaVersion,
            MaxOutputTokens: 500,
            MaxAttempts: maxAttempts,
            Metadata: null);

    private static PimDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static AiGateway CreateGateway(PimDbContext db, FakeChatClient fakeClient, bool enabled, IAiSchemaRegistry? registry = null)
    {
        var options = Options.Create(new AiOptions
        {
            Enabled = enabled,
            Provider = "litellm",
            BaseUrl = "http://litellm:4000",
            ApiKey = "sk-pim",
            DefaultModel = "pim-default",
            TimeoutSeconds = 30,
            MaxOutputTokensPerRequest = 1000,
            MaxAttemptsPerRequest = 2,
            SaveFullPrompts = true,
            SaveFullResponses = true
        });

        return new AiGateway(
            options,
            new FixedAiChatClientFactory(fakeClient),
            registry ?? new AiSchemaRegistry(),
            new AiRequestLogWriter(db));
    }

    private sealed class FixedAiChatClientFactory(IChatClient client) : IAiChatClientFactory
    {
        public IChatClient Create(string model) => client;
    }
}

internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<string> _responses;
    private readonly int? _promptTokens;
    private readonly int? _completionTokens;

    public FakeChatClient(string response, int? promptTokens = null, int? completionTokens = null)
        : this([response], promptTokens, completionTokens)
    {
    }

    public FakeChatClient(IEnumerable<string> responses, int? promptTokens = null, int? completionTokens = null)
    {
        _responses = new Queue<string>(responses);
        _promptTokens = promptTokens;
        _completionTokens = completionTokens;
    }

    public int CallCount { get; private set; }
    public List<IList<ChatMessage>> Requests { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add(messages.ToList());
        var response = _responses.Count > 0 ? _responses.Dequeue() : string.Empty;
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = _promptTokens,
                OutputTokenCount = _completionTokens,
                TotalTokenCount = _promptTokens + _completionTokens
            }
        };
        return Task.FromResult(chatResponse);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => EmptyStreamingResponse();

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStreamingResponse()
    {
        await Task.CompletedTask;
        yield break;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
