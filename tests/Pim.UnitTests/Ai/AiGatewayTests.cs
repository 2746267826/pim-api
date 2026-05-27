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

    [Fact]
    public async Task CompleteAsync_UsesConfiguredTimeoutAndLogsTimedOut()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient("late answer", waitForCancellation: true);
        var gateway = CreateGateway(db, fakeClient, enabled: true, timeoutSeconds: 1);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null));

        Assert.Equal(AiRequestStatus.TimedOut, result.Status);
        Assert.Equal("AI request timed out.", result.UserFacingError);
        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Equal("timed_out", log.Status);
        Assert.Equal("timed_out", log.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_RetriesTimeoutAndLogsEachAttempt_WhenRetrySucceeds()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient(
            [
                FakeChatClientStep.WaitUntilCanceled(),
                FakeChatClientStep.Respond("plain answer")
            ]);
        var gateway = CreateGateway(db, fakeClient, enabled: true, timeoutSeconds: 1);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null, maxAttempts: 2));

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("plain answer", result.ResponseText);
        Assert.Equal(2, fakeClient.CallCount);
        var logs = await db.AiRequestLogs.OrderBy(log => log.AttemptNumber).ToListAsync();
        Assert.Collection(
            logs,
            log =>
            {
                Assert.Equal("timed_out", log.Status);
                Assert.Equal(1, log.AttemptNumber);
                Assert.Equal(2, log.MaxAttempts);
                Assert.Equal("timed_out", log.ErrorCode);
            },
            log =>
            {
                Assert.Equal("succeeded", log.Status);
                Assert.Equal(2, log.AttemptNumber);
                Assert.Equal(2, log.MaxAttempts);
                Assert.Null(log.ErrorCode);
            });
    }

    [Fact]
    public async Task CompleteAsync_PropagatesCallerCancellationAndDoesNotLogAttempt()
    {
        var fakeClient = new FakeChatClient([FakeChatClientStep.WaitUntilCanceled()]);
        var logWriter = new FailingAiRequestLogWriter();
        var gateway = CreateGateway(fakeClient, logWriter, enabled: true, timeoutSeconds: 30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = await Record.ExceptionAsync(() =>
            gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null, maxAttempts: 2), cts.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.Equal(1, fakeClient.CallCount);
        Assert.Equal(0, logWriter.WriteCount);
    }

    [Fact]
    public async Task CompleteAsync_LogsFailed_WhenProviderFactoryThrows()
    {
        await using var db = CreateDb();
        var gateway = CreateGateway(
            db,
            new ThrowingAiChatClientFactory(new InvalidOperationException("provider misconfigured")),
            enabled: true);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null));

        Assert.Equal(AiRequestStatus.Failed, result.Status);
        Assert.Equal("AI provider is unavailable.", result.UserFacingError);
        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Equal("failed", log.Status);
        Assert.Equal("provider_unavailable", log.ErrorCode);
        Assert.Contains("provider misconfigured", log.ErrorMessage);
    }

    [Fact]
    public async Task CompleteAsync_RetriesProviderFailureAndLogsEachAttempt_WhenRetrySucceeds()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient(
            [
                FakeChatClientStep.Throw(new InvalidOperationException("provider hiccup")),
                FakeChatClientStep.Respond("plain answer")
            ]);
        var gateway = CreateGateway(db, fakeClient, enabled: true);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null, maxAttempts: 2));

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("plain answer", result.ResponseText);
        Assert.Equal(2, fakeClient.CallCount);
        var logs = await db.AiRequestLogs.OrderBy(log => log.AttemptNumber).ToListAsync();
        Assert.Collection(
            logs,
            log =>
            {
                Assert.Equal("failed", log.Status);
                Assert.Equal(1, log.AttemptNumber);
                Assert.Equal(2, log.MaxAttempts);
                Assert.Equal("provider_unavailable", log.ErrorCode);
                Assert.Contains("provider hiccup", log.ErrorMessage);
            },
            log =>
            {
                Assert.Equal("succeeded", log.Status);
                Assert.Equal(2, log.AttemptNumber);
                Assert.Equal(2, log.MaxAttempts);
                Assert.Null(log.ErrorCode);
            });
    }

    [Fact]
    public async Task CompleteAsync_ClampsConfiguredMaxAttemptsToAtLeastOne()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient("plain answer");
        var gateway = CreateGateway(
            db,
            fakeClient,
            enabled: true,
            maxAttemptsPerRequest: 0);

        var result = await gateway.CompleteAsync(BasicRequest(schemaName: null, schemaVersion: null));

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal(1, fakeClient.CallCount);
        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Equal("succeeded", log.Status);
        Assert.Equal(1, log.AttemptNumber);
        Assert.Equal(1, log.MaxAttempts);
    }

    [Fact]
    public async Task CompleteAsync_HonorsPromptAndResponsePersistenceSwitches()
    {
        await using var db = CreateDb();
        var fakeClient = new FakeChatClient("""{"title":"Secret response"}""");
        var registry = new AiSchemaRegistry();
        registry.Register(new AiSchemaDefinition(
            "quick-note-conversion",
            "1",
            """{"type":"object","required":["title"],"properties":{"title":{"type":"string"}}}""",
            "Quick note conversion"));
        var gateway = CreateGateway(
            db,
            fakeClient,
            enabled: true,
            registry: registry,
            saveFullPrompts: false,
            saveFullResponses: false);

        var result = await gateway.CompleteAsync(BasicRequest());

        Assert.Equal(AiRequestStatus.Succeeded, result.Status);
        Assert.Equal("""{"title":"Secret response"}""", result.ResponseText);
        Assert.Equal("""{"title":"Secret response"}""", result.ParsedOutputJson);
        var log = await db.AiRequestLogs.SingleAsync();
        Assert.Equal("[]", log.RequestMessagesJson);
        Assert.DoesNotContain("convert this note", log.RequestPayloadJson);
        Assert.Equal("{}", log.ResponseRawJson);
        Assert.Null(log.ResponseText);
        Assert.Null(log.ParsedOutputJson);
    }

    [Fact]
    public void AiChatClientFactory_CachesClientByModel()
    {
        var factory = new AiChatClientFactory(Options.Create(new AiOptions
        {
            BaseUrl = "http://litellm:4000",
            ApiKey = "sk-pim"
        }));

        using (factory)
        {
            var first = factory.Create("pim-default");
            var second = factory.Create("pim-default");
            var other = factory.Create("other-model");

            Assert.Same(first, second);
            Assert.NotSame(first, other);
        }
    }

    [Fact]
    public void AiChatClientFactory_DisposesCachedClientsAndRejectsCreateAfterDispose()
    {
        var factory = new CountingAiChatClientFactory();
        var client = factory.Create("pim-default");

        factory.Dispose();

        var disposable = Assert.IsType<DisposableFakeChatClient>(client);
        Assert.True(disposable.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => factory.Create("pim-default"));
    }

    [Fact]
    public async Task AiChatClientFactory_ConcurrentCreateForSameModelCreatesOneClient()
    {
        var factory = new CountingAiChatClientFactory(delayCreation: true);

        var clients = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => factory.Create("pim-default"))));

        Assert.Equal(1, factory.CreateCount);
        Assert.All(clients, client => Assert.Same(clients[0], client));
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

    private static AiGateway CreateGateway(
        PimDbContext db,
        FakeChatClient fakeClient,
        bool enabled,
        IAiSchemaRegistry? registry = null,
        int timeoutSeconds = 30,
        int maxAttemptsPerRequest = 2,
        bool saveFullPrompts = true,
        bool saveFullResponses = true)
        => CreateGateway(
            db,
            new FixedAiChatClientFactory(fakeClient),
            enabled,
            registry,
            timeoutSeconds,
            maxAttemptsPerRequest,
            saveFullPrompts,
            saveFullResponses);

    private static AiGateway CreateGateway(
        PimDbContext db,
        IAiChatClientFactory factory,
        bool enabled,
        IAiSchemaRegistry? registry = null,
        int timeoutSeconds = 30,
        int maxAttemptsPerRequest = 2,
        bool saveFullPrompts = true,
        bool saveFullResponses = true)
    {
        var options = Options.Create(new AiOptions
        {
            Enabled = enabled,
            Provider = "litellm",
            BaseUrl = "http://litellm:4000",
            ApiKey = "sk-pim",
            DefaultModel = "pim-default",
            TimeoutSeconds = timeoutSeconds,
            MaxOutputTokensPerRequest = 1000,
            MaxAttemptsPerRequest = maxAttemptsPerRequest,
            SaveFullPrompts = saveFullPrompts,
            SaveFullResponses = saveFullResponses
        });

        return new AiGateway(
            options,
            factory,
            registry ?? new AiSchemaRegistry(),
            new AiRequestLogWriter(db));
    }

    private static AiGateway CreateGateway(
        FakeChatClient fakeClient,
        IAiRequestLogWriter logWriter,
        bool enabled,
        IAiSchemaRegistry? registry = null,
        int timeoutSeconds = 30,
        int maxAttemptsPerRequest = 2,
        bool saveFullPrompts = true,
        bool saveFullResponses = true)
    {
        var options = Options.Create(new AiOptions
        {
            Enabled = enabled,
            Provider = "litellm",
            BaseUrl = "http://litellm:4000",
            ApiKey = "sk-pim",
            DefaultModel = "pim-default",
            TimeoutSeconds = timeoutSeconds,
            MaxOutputTokensPerRequest = 1000,
            MaxAttemptsPerRequest = maxAttemptsPerRequest,
            SaveFullPrompts = saveFullPrompts,
            SaveFullResponses = saveFullResponses
        });

        return new AiGateway(
            options,
            new FixedAiChatClientFactory(fakeClient),
            registry ?? new AiSchemaRegistry(),
            logWriter);
    }

    private sealed class FixedAiChatClientFactory(IChatClient client) : IAiChatClientFactory
    {
        public IChatClient Create(string model) => client;
    }

    private sealed class ThrowingAiChatClientFactory(Exception exception) : IAiChatClientFactory
    {
        public IChatClient Create(string model) => throw exception;
    }

    private sealed class FailingAiRequestLogWriter : IAiRequestLogWriter
    {
        public int WriteCount { get; private set; }

        public Task<Guid> WriteAsync(AiRequestLogWriteModel model, CancellationToken ct = default)
        {
            WriteCount++;
            throw new InvalidOperationException("Caller cancellation should not be logged as an AI attempt.");
        }
    }
}

internal sealed class CountingAiChatClientFactory(bool delayCreation = false)
    : AiChatClientFactory(Options.Create(new AiOptions
    {
        BaseUrl = "http://litellm:4000",
        ApiKey = "sk-pim"
    }))
{
    private int _createCount;

    public int CreateCount => _createCount;

    protected override IChatClient CreateClientCore(string model)
    {
        Interlocked.Increment(ref _createCount);
        if (delayCreation)
        {
            Thread.Sleep(50);
        }

        return new DisposableFakeChatClient();
    }
}

internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<FakeChatClientStep> _steps;
    private readonly int? _promptTokens;
    private readonly int? _completionTokens;

    public FakeChatClient(string response, int? promptTokens = null, int? completionTokens = null, bool waitForCancellation = false)
        : this(
            waitForCancellation
                ? [FakeChatClientStep.WaitUntilCanceled()]
                : [FakeChatClientStep.Respond(response)],
            promptTokens,
            completionTokens)
    {
    }

    public FakeChatClient(IEnumerable<string> responses, int? promptTokens = null, int? completionTokens = null, bool waitForCancellation = false)
        : this(
            waitForCancellation
                ? responses.Select(_ => FakeChatClientStep.WaitUntilCanceled())
                : responses.Select(FakeChatClientStep.Respond),
            promptTokens,
            completionTokens)
    {
    }

    public FakeChatClient(IEnumerable<FakeChatClientStep> steps, int? promptTokens = null, int? completionTokens = null)
    {
        _steps = new Queue<FakeChatClientStep>(steps);
        _promptTokens = promptTokens;
        _completionTokens = completionTokens;
    }

    public int CallCount { get; private set; }
    public List<IList<ChatMessage>> Requests { get; } = [];

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        CallCount++;
        Requests.Add(messages.ToList());
        var step = _steps.Count > 0 ? _steps.Dequeue() : FakeChatClientStep.Respond(string.Empty);
        if (step.Exception is not null)
        {
            throw step.Exception;
        }

        if (step.WaitForCancellation && cancellationToken.CanBeCanceled)
        {
            await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
        }

        var response = step.Response ?? string.Empty;
        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, response))
        {
            Usage = new UsageDetails
            {
                InputTokenCount = _promptTokens,
                OutputTokenCount = _completionTokens,
                TotalTokenCount = _promptTokens + _completionTokens
            }
        };
        return chatResponse;
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

internal sealed record FakeChatClientStep(string? Response, bool WaitForCancellation, Exception? Exception)
{
    public static FakeChatClientStep Respond(string response) => new(response, false, null);

    public static FakeChatClientStep WaitUntilCanceled() => new(null, true, null);

    public static FakeChatClientStep Throw(Exception exception) => new(null, false, exception);
}

internal sealed class DisposableFakeChatClient : IChatClient, IDisposable
{
    public bool IsDisposed { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)));

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => EmptyStreamingResponse();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
        IsDisposed = true;
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStreamingResponse()
    {
        await Task.CompletedTask;
        yield break;
    }
}
