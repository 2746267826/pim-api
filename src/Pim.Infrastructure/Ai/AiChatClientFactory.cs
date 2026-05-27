using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;
using System.ClientModel;

namespace Pim.Infrastructure.Ai;

public interface IAiChatClientFactory
{
    IChatClient Create(string model);
}

public sealed class AiChatClientFactory(IOptions<AiOptions> options) : IAiChatClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public IChatClient Create(string model)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _clients.GetOrAdd(model, CreateClient);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var client in _clients.Values)
        {
            if (client is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _clients.Clear();
    }

    private IChatClient CreateClient(string model)
    {
        var ai = options.Value;
        var chatClient = new ChatClient(
            model: model,
            credential: new ApiKeyCredential(ai.ApiKey),
            options: new OpenAIClientOptions
            {
                Endpoint = new Uri(ai.BaseUrl.TrimEnd('/') + "/v1")
            });

        return chatClient.AsIChatClient();
    }
}
