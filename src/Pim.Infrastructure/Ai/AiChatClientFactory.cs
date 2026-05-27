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

public sealed class AiChatClientFactory(IOptions<AiOptions> options) : IAiChatClientFactory
{
    public IChatClient Create(string model)
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
