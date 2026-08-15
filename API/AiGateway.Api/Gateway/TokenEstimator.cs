using AiGateway.Api.Models;
using Microsoft.ML.Tokenizers;

namespace AiGateway.Api.Gateway;

/// <summary>Produces a pre-call token estimate using a real BPE tokenizer (Microsoft.ML.Tokenizers'
/// cl100k_base implementation, the encoding used by GPT-3.5/GPT-4-generation models) rather than a
/// char-count heuristic. It is still an ESTIMATE, for three reasons worth stating plainly:
/// (1) not every configured model actually uses cl100k_base — Anthropic and newer OpenAI models
/// use different tokenizers this gateway doesn't have offline access to; (2) chat-format framing
/// (role markers, message boundaries) adds a small, provider-specific overhead on top of raw text
/// tokens that this only approximates; (3) it never sees system-level prompt injection or tool
/// definitions a provider might add server-side. TokenBudgetService reserves against this
/// estimate and reconciles with the provider-reported UsageInfo once a call completes.</summary>
public interface ITokenEstimator
{
    int EstimateInputTokens(IReadOnlyList<ChatTurn> messages);
}

public sealed class TokenEstimator : ITokenEstimator
{
    // Per-message chat-format overhead, consistent with OpenAI's documented approximation for
    // cl100k-family chat models (each turn costs a few tokens beyond its raw text content).
    private const int PerTurnOverheadTokens = 4;

    private readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public int EstimateInputTokens(IReadOnlyList<ChatTurn> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += _tokenizer.CountTokens(message.Content) + PerTurnOverheadTokens;
        }

        return total;
    }
}
