using System.ClientModel;
using System.Collections.Concurrent;
using AiGateway.Api.Models;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AiGateway.Api.Providers;

/// <summary>Real provider implementation talking to any OpenAI-shaped chat completions API,
/// built on <c>Microsoft.Extensions.AI</c>'s <see cref="IChatClient"/> abstraction rather than a
/// hand-rolled HTTP client — this is the officially documented way to reach OpenAI (or an
/// OpenAI-compatible endpoint) from .NET today. One <see cref="OpenAIClient"/> serves every
/// model tier configured against this provider; a per-model-id <see cref="IChatClient"/> is
/// created lazily and cached, since <c>GetChatClient</c> binds to a specific model id.
/// Not invoked with live traffic anywhere in this repository — no API key is configured by
/// default, so <c>Program.cs</c> falls back to <see cref="FakeAiProvider"/> instead.</summary>
public sealed class OpenAiProvider : IAiProvider
{
    private readonly OpenAIClient _client;
    private readonly ConcurrentDictionary<string, IChatClient> _chatClients = new();

    public OpenAiProvider(string apiKey)
    {
        _client = new OpenAIClient(new ApiKeyCredential(apiKey));
        Name = "OpenAI";
    }

    public string Name { get; }

    public async Task<ProviderCompletionResult> CompleteAsync(
        NormalizedChatRequest request,
        string providerModelId,
        int maxOutputTokens,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var chatClient = _chatClients.GetOrAdd(providerModelId, id => _client.GetChatClient(id).AsIChatClient());
        var messages = request.Messages.Select(ToChatMessage).ToList();

        try
        {
            var response = await chatClient.GetResponseAsync(
                messages,
                new ChatOptions { MaxOutputTokens = maxOutputTokens },
                linkedCts.Token);

            var usage = response.Usage;
            return new ProviderCompletionResult(
                response.Text,
                new UsageInfo((int)(usage?.InputTokenCount ?? 0), (int)(usage?.OutputTokenCount ?? 0)),
                response.FinishReason?.Value ?? "stop");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(Name, ProviderFailureKind.Timeout,
                $"Provider '{Name}' timed out after {timeout.TotalSeconds:0}s.");
        }
        catch (ClientResultException ex)
        {
            var kind = ex.Status switch
            {
                429 => ProviderFailureKind.RateLimited,
                401 or 403 => ProviderFailureKind.AuthenticationFailed,
                >= 500 => ProviderFailureKind.ServerError,
                _ => ProviderFailureKind.InvalidRequest
            };
            throw new AiProviderException(Name, kind, ex.Message, ex.Status, ex);
        }
    }

    private static ChatMessage ToChatMessage(ChatTurn turn) => new(
        turn.Role switch
        {
            GatewayChatRole.System => ChatRole.System,
            GatewayChatRole.Assistant => ChatRole.Assistant,
            _ => ChatRole.User
        },
        turn.Content);
}
