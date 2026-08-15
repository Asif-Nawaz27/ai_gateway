using System.Collections.Concurrent;
using AiGateway.Api.Options;

namespace AiGateway.Api.Gateway;

public sealed record BudgetReservation(string ReservationId, int ReservedTokens);

public sealed record TenantBudgetStatus(int BudgetLimit, int ConsumedTokens, int RemainingTokens);

/// <summary>Enforces a per-tenant token budget — how much model usage a tenant may consume —
/// which is a materially different control from the ASP.NET Core request-rate limiter (how many
/// HTTP requests a tenant may make). See RateLimiting/ for the request-rate side.</summary>
public interface ITokenBudgetService
{
    /// <summary>Reserves <paramref name="estimatedInputTokens"/> + <paramref name="maxOutputTokens"/>
    /// against the tenant's budget immediately, before any provider call is made, so concurrent
    /// requests can't race past the limit. Throws <see cref="BudgetExceededException"/> if the
    /// reservation would exceed the budget.</summary>
    BudgetReservation Reserve(string tenantId, TenantOptions tenant, int estimatedInputTokens, int maxOutputTokens);

    /// <summary>Replaces the reservation's estimated token count with the provider-reported
    /// actual usage, once a call succeeds.</summary>
    void Commit(string tenantId, string reservationId, int actualTotalTokens);

    /// <summary>Releases a reservation that never resulted in billable usage (the whole fallback
    /// chain failed, or the request was rejected after the reservation was made).</summary>
    void Release(string tenantId, string reservationId);

    TenantBudgetStatus GetStatus(string tenantId, TenantOptions tenant);
}

public sealed class BudgetExceededException : Exception
{
    public string TenantId { get; }
    public int BudgetLimit { get; }
    public int Consumed { get; }
    public int Requested { get; }

    public BudgetExceededException(string tenantId, int budgetLimit, int consumed, int requested)
        : base($"Tenant '{tenantId}' would exceed its token budget: {consumed + requested}/{budgetLimit} tokens.")
    {
        TenantId = tenantId;
        BudgetLimit = budgetLimit;
        Consumed = consumed;
        Requested = requested;
    }
}

/// <summary>In-memory, single-instance implementation using a reserve-then-reconcile pattern
/// over a rolling 24-hour window. "Daily" here means "in the trailing 24 hours", not "reset at
/// UTC midnight" — that avoids every tenant's budget resetting simultaneously and creating a
/// burst at the boundary. Not safe across multiple gateway instances as-is; the production notes
/// in the article cover moving this to Redis (e.g. a sorted set per tenant, or a Lua script for
/// atomic check-and-reserve) without changing the ITokenBudgetService contract.</summary>
public sealed class TokenBudgetService : ITokenBudgetService
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, TenantState> _tenants = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _timeProvider;

    public TokenBudgetService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private sealed class TenantState
    {
        public readonly object Gate = new();
        public readonly Dictionary<string, Entry> Entries = new();
    }

    private sealed class Entry
    {
        public required DateTimeOffset Timestamp { get; init; }
        public int ReservedTokens { get; init; }
        public int? ActualTokens { get; set; }
        public int EffectiveTokens => ActualTokens ?? ReservedTokens;
    }

    public BudgetReservation Reserve(string tenantId, TenantOptions tenant, int estimatedInputTokens, int maxOutputTokens)
    {
        var state = _tenants.GetOrAdd(tenantId, _ => new TenantState());
        var requested = estimatedInputTokens + maxOutputTokens;

        lock (state.Gate)
        {
            Prune(state);
            var consumed = state.Entries.Values.Sum(e => e.EffectiveTokens);
            if (consumed + requested > tenant.DailyTokenBudget)
            {
                throw new BudgetExceededException(tenantId, tenant.DailyTokenBudget, consumed, requested);
            }

            var id = Guid.NewGuid().ToString("N");
            state.Entries[id] = new Entry { Timestamp = _timeProvider.GetUtcNow(), ReservedTokens = requested };
            return new BudgetReservation(id, requested);
        }
    }

    public void Commit(string tenantId, string reservationId, int actualTotalTokens)
    {
        if (!_tenants.TryGetValue(tenantId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            if (state.Entries.TryGetValue(reservationId, out var entry))
            {
                entry.ActualTokens = actualTotalTokens;
            }
        }
    }

    public void Release(string tenantId, string reservationId)
    {
        if (!_tenants.TryGetValue(tenantId, out var state))
        {
            return;
        }

        lock (state.Gate)
        {
            state.Entries.Remove(reservationId);
        }
    }

    public TenantBudgetStatus GetStatus(string tenantId, TenantOptions tenant)
    {
        var state = _tenants.GetOrAdd(tenantId, _ => new TenantState());
        lock (state.Gate)
        {
            Prune(state);
            var consumed = state.Entries.Values.Sum(e => e.EffectiveTokens);
            return new TenantBudgetStatus(tenant.DailyTokenBudget, consumed, Math.Max(0, tenant.DailyTokenBudget - consumed));
        }
    }

    private void Prune(TenantState state)
    {
        var cutoff = _timeProvider.GetUtcNow() - Window;
        List<string>? expired = null;
        foreach (var (key, entry) in state.Entries)
        {
            if (entry.Timestamp < cutoff)
            {
                (expired ??= new List<string>()).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (var key in expired)
        {
            state.Entries.Remove(key);
        }
    }
}
