using AiGateway.Api.Models;

namespace AiGateway.Api.Providers;

/// <summary>Everything the gateway depends on to call a model. The gateway never talks to an
/// OpenAI/Anthropic/local SDK directly — only to this abstraction — so a new provider can be
/// added without touching ModelRouter, TokenBudgetService, or AiGatewayService.</summary>
public interface IAiProvider
{
    /// <summary>Matches the "Provider" value in ModelOptions and the name providers register
    /// themselves under in DI. Also used as the partition key for provider health tracking.</summary>
    string Name { get; }

    Task<ProviderCompletionResult> CompleteAsync(
        NormalizedChatRequest request,
        string providerModelId,
        int maxOutputTokens,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
