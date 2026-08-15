namespace AiGateway.Api.Models;

// Wire-level DTOs for POST /api/ai/chat. Kept separate from the internal gateway types
// (GatewayTypes.cs) so the public contract can evolve independently of routing internals.

public sealed record ChatMessageDto(string Role, string Content);

public sealed record ChatCompletionApiRequest(
    List<ChatMessageDto> Messages,
    string? Model,
    string? Capability,
    string? Priority,
    int? MaxTokens);

public sealed record UsageDto(int InputTokens, int OutputTokens, int TotalTokens, bool Estimated);

public sealed record ChatCompletionApiResponse(
    string RequestId,
    string Model,
    string Provider,
    UsageDto Usage,
    decimal EstimatedCost,
    bool FallbackUsed,
    int RetryCount,
    string RoutingReason,
    string Response);

public sealed record GatewayErrorResponse(string Error, string Reason, string Detail, string RequestId);
