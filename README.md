# AI Gateway (ASP.NET Core)

A reference implementation of an AI Gateway sitting between client applications and multiple LLM
providers: model routing, token budgets, cost tracking, ASP.NET Core rate limiting, bounded
retry/fallback, provider health tracking, and OpenTelemetry observability. Companion code for the
article *"Building an AI Gateway in ASP.NET Core: Model Routing, Token Budgets, Rate Limits, and
Fallbacks."*

No API keys are required to run this end to end — see [Providers and secrets](#providers-and-secrets).

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (optional — only needed if you want a Jaeger UI for traces; see [Observability](#observability))

## Project structure

```
AiGateway/
├── src/AiGateway.Api/          ASP.NET Core Web API — the gateway itself
│   ├── Options/                 Strongly-typed configuration (models, tenants, gateway policy)
│   ├── Models/                  Wire-level DTOs and internal gateway types
│   ├── Providers/                IAiProvider + OpenAI/Anthropic/Fake implementations
│   ├── Gateway/                  Router, budget, cost, health, orchestrator
│   ├── RateLimiting/              ASP.NET Core rate limiter wiring
│   ├── Middleware/                Tenant authentication endpoint filter
│   ├── Observability/             Custom metrics + tracing
│   ├── Composition/                DI wiring shared across the API, benchmark, and failure-sim apps
│   └── Endpoints/                  POST /api/ai/chat, GET /api/ai/providers/health
├── tests/AiGateway.Tests/        xUnit — routing, budget, cost, health, resilience, rate limiting
├── failuresim/AiGateway.FailureSim/  Console app: scripted provider failures, printed transcript
├── benchmark/AiGateway.Benchmark/     Console app: Direct-vs-Gateway workload comparison
└── docker-compose.yml             Optional Jaeger for viewing OpenTelemetry traces
```

## Configuration

Routing policy, model tiers, and tenants are all in `appsettings.json` / `appsettings.Development.json`
under the `AiGateway` section — nothing is hard-coded. Model pricing (`InputCostPerMillionTokens`,
`OutputCostPerMillionTokens`) in this repository is **illustrative demo data, not live provider
pricing** — replace it with real figures from your provider's current pricing page before using
cost figures for anything real.

Three demo tenants ship in `appsettings.Development.json`, modeling the scenario the article walks
through:

| Tenant | Profile | Daily token budget | Requests/min | Concurrent AI calls | Allowed models |
|---|---|---:|---:|---:|---|
| `tenant-a` | cost-sensitive | 50,000 | 20 | 3 | economy, standard, local |
| `tenant-b` | latency-sensitive, high volume | 300,000 | 100 | 10 | economy, standard, local |
| `tenant-c` | internal engineering, complex requests allowed | 1,000,000 | 30 | 5 | economy, standard, premium, local |

## Providers and secrets

**No provider API keys are configured by default.** `Program.cs` checks for
`AiGateway:Providers:OpenAI:ApiKey` / `AiGateway:Providers:Anthropic:ApiKey` at startup; if either
is missing, that provider is automatically backed by an in-process `FakeAiProvider` instead of a
real HTTP call. This means `dotnet run` works immediately, with no signup, no keys, and no cost —
the entire request pipeline (routing, budgets, retries, fallback, rate limiting) runs for real,
just against simulated model responses.

To point the gateway at real providers instead, set the keys via **user-secrets** (development) or
environment variables (anywhere else) — never in `appsettings.json`:

```bash
cd src/AiGateway.Api
dotnet user-secrets init
dotnet user-secrets set "AiGateway:Providers:OpenAI:ApiKey" "sk-..."
dotnet user-secrets set "AiGateway:Providers:Anthropic:ApiKey" "sk-ant-..."
```

Or as environment variables (note the `__` double-underscore, ASP.NET Core's convention for
nested configuration keys):

```bash
export AiGateway__Providers__OpenAI__ApiKey="sk-..."
export AiGateway__Providers__Anthropic__ApiKey="sk-ant-..."
```

In production, use your platform's secret manager (Azure Key Vault, AWS Secrets Manager, etc.) —
user-secrets is a *development-only* mechanism.

## Running the application

```bash
cd src/AiGateway.Api
dotnet run
```

By default this listens on `http://localhost:5017` (see `Properties/launchSettings.json`). Try it:

```bash
curl -s -X POST http://localhost:5017/api/ai/chat \
  -H "Content-Type: application/json" \
  -H "X-Api-Key: dev-tenant-c-key" \
  -d '{
    "messages": [{"role": "user", "content": "Explain dependency injection in ASP.NET Core."}],
    "model": "auto",
    "capability": "complex",
    "priority": "normal",
    "maxTokens": 300
  }'
```

Check current provider health:

```bash
curl -s http://localhost:5017/api/ai/providers/health
```

Application health (distinct from AI provider health — see the article's health-checks section):

```bash
curl -s http://localhost:5017/health
```

## Running tests

```bash
dotnet test tests/AiGateway.Tests/AiGateway.Tests.csproj
```

Covers routing decisions, token budget enforcement, cost calculation, provider health
classification, retry/fallback behavior for every failure kind, and rate-limiting/concurrency
behavior through the real ASP.NET Core pipeline (`WebApplicationFactory`). No test calls a real
paid API.

## Simulating provider failures

```bash
dotnet run --project failuresim/AiGateway.FailureSim
```

Scripts specific provider behaviors (429, 500, 503, timeout, malformed response, malformed
request, and a sustained failure streak that trips the health tracker) against the real
`AiGatewayService` and prints exactly what the gateway decided at each step — the transcript this
produces is quoted directly in the article's failure-simulation section.

## Running the benchmark

```bash
dotnet run -c Release --project benchmark/AiGateway.Benchmark
```

Runs the same simulated workload through a "Direct-to-Premium" path and the full gateway, and
prints latency/success-rate/cost/token comparisons. Read the comments at the top of
`benchmark/AiGateway.Benchmark/Program.cs` for exactly what is and isn't included in the numbers
(no real network calls, no ASP.NET Core HTTP transport — see the article's benchmark section for
the full methodology and a captured run).

## Observability

Traces and metrics are emitted via OpenTelemetry with a console exporter enabled by default — run
the app and watch the console for `Activity`/metric dumps. To view traces in a real UI instead:

```bash
docker compose up -d
```

Then set an OTLP endpoint before running the app:

```bash
export OpenTelemetry__OtlpEndpoint="http://localhost:4317"
dotnet run --project src/AiGateway.Api
```

Open the Jaeger UI at http://localhost:16686. Metric names are prefixed `ai_gateway_*` and are
custom to this project (not OpenTelemetry semantic-convention names) — see
`src/AiGateway.Api/Observability/AiGatewayMetrics.cs` for the full list and what each one means.

## Security notes

- Tenant identification here is a static per-tenant API key (`X-Api-Key` header) — a deliberate
  simplification for a sample, not a production authentication scheme. See the article's security
  section for what to use instead.
- Clients select a model **tier key** (`economy`/`standard`/`premium`/`auto`), never a raw
  provider model ID or provider URL — this is the allowlist that prevents model-selection abuse
  and rules out SSRF via a client-supplied endpoint.
- Prompts and completions are never logged in full — only lengths/token counts. See
  `AiGatewayService` and the structured log calls throughout `Gateway/`.
- Provider API keys are never returned to clients and are only ever read from user-secrets/environment
  variables, never from `appsettings.json`.
