using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AiGateway.Api.Models;

namespace AiGateway.Api.Providers;

/// <summary>Real provider implementation talking to Anthropic's Messages API directly over
/// <see cref="HttpClient"/> — deliberately not wrapped in an SDK abstraction, unlike
/// <see cref="OpenAiProvider"/>. The gateway still normalizes both into the same
/// <see cref="ProviderCompletionResult"/> shape, which is the point: <c>IAiProvider</c> doesn't
/// care whether a provider is reached through a client library or raw REST calls. The
/// <see cref="HttpClient"/> is created by <c>IHttpClientFactory</c> under the name "Anthropic" and
/// carries the transport-level resilience handler registered in Program.cs (retry/circuit-breaker
/// for the HTTP call itself, distinct from the gateway-level model fallback in AiGatewayService).
/// Not invoked with live traffic anywhere in this repository.</summary>
public sealed partial class AnthropicProvider : IAiProvider
{
    private const string ApiVersion = "2023-06-01";
    private readonly HttpClient _httpClient;

    public AnthropicProvider(HttpClient httpClient, string apiKey)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress ??= new Uri("https://api.anthropic.com/");
        _httpClient.DefaultRequestHeaders.Remove("x-api-key");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _httpClient.DefaultRequestHeaders.Remove("anthropic-version");
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", ApiVersion);
        Name = "Anthropic";
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

        var systemPrompt = string.Join("\n", request.Messages
            .Where(m => m.Role == GatewayChatRole.System)
            .Select(m => m.Content));

        var turns = request.Messages
            .Where(m => m.Role != GatewayChatRole.System)
            .Select(m => new AnthropicMessage(m.Role == GatewayChatRole.Assistant ? "assistant" : "user", m.Content))
            .ToList();

        var payload = new AnthropicRequest(
            providerModelId,
            maxOutputTokens,
            turns,
            string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt);

        HttpResponseMessage httpResponse;
        try
        {
            httpResponse = await _httpClient.PostAsJsonAsync("v1/messages", payload, AnthropicJsonContext.Default.AnthropicRequest, linkedCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(Name, ProviderFailureKind.Timeout,
                $"Provider '{Name}' timed out after {timeout.TotalSeconds:0}s.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(Name, ProviderFailureKind.ServerError, $"Provider '{Name}' network failure: {ex.Message}", inner: ex);
        }

        if (!httpResponse.IsSuccessStatusCode)
        {
            var status = (int)httpResponse.StatusCode;
            var kind = status switch
            {
                429 => ProviderFailureKind.RateLimited,
                401 or 403 => ProviderFailureKind.AuthenticationFailed,
                >= 500 => ProviderFailureKind.ServerError,
                _ => ProviderFailureKind.InvalidRequest
            };
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            throw new AiProviderException(Name, kind, $"Provider '{Name}' returned HTTP {status}: {body}", status);
        }

        var parsed = await httpResponse.Content.ReadFromJsonAsync(AnthropicJsonContext.Default.AnthropicResponse, cancellationToken);
        if (parsed is null || parsed.Content.Count == 0)
        {
            throw new AiProviderException(Name, ProviderFailureKind.InvalidResponse, $"Provider '{Name}' returned an empty or malformed response.");
        }

        var text = string.Concat(parsed.Content.Select(c => c.Text));
        var usage = new UsageInfo(parsed.Usage.InputTokens, parsed.Usage.OutputTokens);
        return new ProviderCompletionResult(text, usage, parsed.StopReason ?? "stop");
    }

    private sealed record AnthropicMessage(string Role, string Content);

    private sealed record AnthropicRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages,
        [property: JsonPropertyName("system")] string? System);

    private sealed record AnthropicResponseContent([property: JsonPropertyName("text")] string Text);

    private sealed record AnthropicUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);

    private sealed record AnthropicResponse(
        [property: JsonPropertyName("content")] List<AnthropicResponseContent> Content,
        [property: JsonPropertyName("usage")] AnthropicUsage Usage,
        [property: JsonPropertyName("stop_reason")] string? StopReason);

    [JsonSerializable(typeof(AnthropicRequest))]
    [JsonSerializable(typeof(AnthropicResponse))]
    private partial class AnthropicJsonContext : JsonSerializerContext
    {
    }
}
