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

public class AiChatClientFactory(IOptions<AiOptions> options) : IAiChatClientFactory, IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IChatClient> _clients = new(StringComparer.Ordinal);
    private bool _disposed;

    public IChatClient Create(string model)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_clients.TryGetValue(model, out var client))
            {
                return client;
            }

            client = CreateClientCore(model);
            _clients.Add(model, client);
            return client;
        }
    }

    public void Dispose()
    {
        lock (_lock)
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
    }

    protected virtual IChatClient CreateClientCore(string model)
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
