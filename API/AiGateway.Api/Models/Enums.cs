namespace AiGateway.Api.Models;

/// <summary>Role of a single chat turn, independent of any specific provider's wire format.</summary>
public enum GatewayChatRole
{
    System,
    User,
    Assistant
}

/// <summary>Client-declared task complexity. Deliberately client-supplied rather than inferred
/// by a classifier — see the "deterministic baseline before ML routing" discussion in the article.</summary>
public enum RequestCapability
{
    Simple,
    Standard,
    Complex
}

/// <summary>Client-declared urgency. Feeds routing's budget-downgrade tolerance and is recorded
/// in telemetry; it does not by itself select a model tier.</summary>
public enum RequestPriority
{
    Low,
    Normal,
    High
}

public enum ProviderHealthStatus
{
    Healthy,
    Degraded,
    Unavailable
}

/// <summary>Normalized failure classification used by the retry/fallback policy. Every
/// <see cref="AiProviderException"/> carries one of these regardless of which provider threw it.</summary>
public enum ProviderFailureKind
{
    RateLimited,
    ServerError,
    Timeout,
    InvalidRequest,
    AuthenticationFailed,
    InvalidResponse,
    Canceled
}

public enum GatewayRejectionReason
{
    None,
    Validation,
    ModelNotPermitted,
    BudgetExceeded,
    ContextTooLarge,
    NoHealthyProvider
}
