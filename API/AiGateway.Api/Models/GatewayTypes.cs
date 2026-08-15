namespace AiGateway.Api.Models;

public sealed record ChatTurn(GatewayChatRole Role, string Content);

/// <summary>The gateway's internal, provider-agnostic representation of a request. Built once
/// per HTTP request and passed unchanged down the routing/retry/fallback chain, so every
/// provider attempt (including fallback attempts) sees identical conversation state.</summary>
public sealed record NormalizedChatRequest(
    string TenantId,
    string RequestId,
    IReadOnlyList<ChatTurn> Messages,
    int MaxOutputTokens,
    int EstimatedInputTokens);

/// <summary>Provider-reported usage, returned only after a completion succeeds. Distinct from
/// the pre-call estimate produced by <c>ITokenEstimator</c> — see the estimate-vs-actual
/// discussion in the token budgets section of the article.</summary>
public sealed record UsageInfo(int InputTokens, int OutputTokens)
{
    public int TotalTokens => InputTokens + OutputTokens;
}

public sealed record ProviderCompletionResult(string Content, UsageInfo Usage, string FinishReason);

/// <summary>Normalized failure raised by any <c>IAiProvider</c> implementation. The gateway's
/// retry/fallback policy only ever inspects <see cref="Kind"/> — it never branches on
/// provider-specific exception types or status codes.</summary>
public sealed class AiProviderException : Exception
{
    public string ProviderName { get; }
    public ProviderFailureKind Kind { get; }
    public int? HttpStatusCode { get; }

    public AiProviderException(
        string providerName,
        ProviderFailureKind kind,
        string message,
        int? httpStatusCode = null,
        Exception? inner = null)
        : base(message, inner)
    {
        ProviderName = providerName;
        Kind = kind;
        HttpStatusCode = httpStatusCode;
    }
}
