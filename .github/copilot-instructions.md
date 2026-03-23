# Copilot Instructions — PlatinumForge

## Build & Run

```bash
cd PlatinumForge
dotnet build
dotnet run          # starts HttpListener on :5005
```

Requires .NET 10 SDK and `OPENAI_API_KEY` env var. No test suite exists — the app *generates and compiles* tests for user projects via Roslyn, but has no tests for itself.

## Architecture

PlatinumForge is a **single-file C# application** (`PlatinumForge/Program.cs`, ~5400 lines). There are no other source files. The entire backend, frontend SPA, and build pipeline live in this one file.

### Major sections (top to bottom)

| Section | What it does |
|---------|--------------|
| **SystemState** | Core data model — 7-layer specification (Intent → Constraints → Shape → Behaviour → Forge → Finetune → Deploy) stored as `Dictionary<string, string>` properties |
| **OpenAIClient** | Static LLM wrapper — POST to OpenAI-compatible endpoint, returns completion text |
| **PromptBuilder** | Assembles system/user prompts from SystemState + quality sliders |
| **CodeCompiler** | Roslyn in-memory compilation — merges interfaces + code + tests into one assembly, runs tests via reflection |
| **InvariantChecker** | Static analysis guards (no-reflection, no-file-io, etc.) |
| **Generator** | 10-stage LLM generation — one async method per stage (manifest → interfaces → tests → code → build → NFR → soak → integration → IaC → publish) |
| **AuthManager** | Multi-provider OAuth2 (Google, Microsoft, GitHub, Facebook, Apple) with HMAC-signed cookies |
| **SseClient / LiveSession** | Real-time collaboration via Server-Sent Events — broadcasts state deltas to all connected clients |
| **PlatinumForgeServer** | HTTP server using raw `HttpListener` — manual path-matching dispatcher for 50+ routes |
| **HTML/JS (embedded)** | Full SPA as C# raw string literals — `LoginPage()` and `HtmlPage()` methods return the complete frontend |
| **Program.Main** | Entry point — configures server, starts listen loop |

### Request flow

```
Browser → HttpListener(:5005) → path/method matching (if/else chain)
  → Auth cookie check → LiveSession lookup → handler method → JSON response
  → SSE broadcast to other clients
```

There is no routing framework. Routes are dispatched via string comparison in a large if/else block inside the server's request handler.

### Data persistence

All state is JSON on disk under `~/.platinumforge/` (configurable via `PLATINUMFORGE_DATA_DIR`):

```
users/{sub}/sessions/{id}/store.json   — SystemState per session
users/{sub}/manifest.json              — session list
users/{sub}/builds.json                — build history
artifacts/{project}/{project}-v{ver}.zip
shares.json                            — share tokens
```

No database. Concurrency managed via `ConcurrentDictionary` and `lock`.

### LLM integration pattern

Every LLM call goes through `OpenAIClient.Complete(systemPrompt, userPrompt)`. The system prompt is dynamically built from the 12 quality sliders (performance, security, readability, etc.) and the user's constraints. LLM output is stripped of markdown fences before use.

## Key Conventions

- **Section headers** use decorated comments: `// ── SectionName ──────────────`
- **No ASP.NET / no frameworks** — raw `HttpListener`, `System.Text.Json`, `Microsoft.CodeAnalysis.CSharp` only
- **Embedded frontend** — HTML, CSS, and JavaScript are C# 11 raw string literals (`"""..."""`) with `$` interpolation and `%%PLACEHOLDER%%` late substitution
- **Fire-and-forget broadcasting** — SSE broadcasts use `_ = Task.Run(...)` or `_ = live.Broadcast(...)`
- **Dual-state model** — generation works on a `proposed` clone of `current` state; changes only commit when compilation + tests pass
- **Error broadcast** — errors surface to the UI via `BroadcastChat("error", message)` rather than throwing to the caller
- **Empty catch blocks** are intentional in many places — they return defaults and let the UI continue

## Environment Variables

| Variable | Required | Purpose |
|----------|----------|---------|
| `OPENAI_API_KEY` | Yes | LLM API key |
| `OPENAI_MODEL` | No | Model name (default: `gpt-4.1`) |
| `OPENAI_ENDPOINT` | No | API endpoint URL |
| `PLATINUMFORGE_DATA_DIR` | No | Data root (default: `~/.platinumforge`) |
| `PLATINUMFORGE_BASE_URL` | No | Base URL for OAuth redirects (default: `http://localhost:5005`) |
| `{GOOGLE,MICROSOFT,GITHUB,FACEBOOK,APPLE}_CLIENT_ID/SECRET` | No | OAuth provider credentials (omit all for open-access local mode) |

## Deployment

Azure Web App in resource group **personal**:

| Resource | Value |
|----------|-------|
| App Service Plan | `ASP-Personal-a002` (B1) |
| Web App | `platinumforge` |
| Live URL | [platinumforge.wavefunctionlabs.com](https://platinumforge.wavefunctionlabs.com) |
