# LlmBenchmark

A C# benchmark tool for local, OpenAI-compatible LLM servers (e.g. **Unsloth Desktop**,
**FreeToken Desktop**). It measures generation speed, time-to-first-token, VRAM usage,
usable context length, and — via [Microsoft Agent Framework](https://learn.microsoft.com/agent-framework/) —
how the model behaves under multi-agent load.

## What it measures

| Mode | What it does | Output |
|---|---|---|
| **Speed & quality pass** | Runs each configured task N times per model; streams each response to capture TTFT, total duration, tokens/s, and VRAM before/after. | `results/speed-*.csv` + raw transcripts |
| **Context probe** | Sends progressively larger prompts (with a random nonce per step so KV-cache prefix reuse can't fake the result) until the server fails or exceeds its device max context. | `results/context-probe-*.csv` |
| **Agent concurrency load** | N identical agents hit the server at once, at each configured concurrency level, via Agent Framework. | `results/agent-concurrency-*.csv` |
| **Multi-agent workflow** | A sequential Planner → Coder → Reviewer pipeline, run as 1..K parallel pipelines. | `results/agent-workflow-*.csv` |

## Requirements

- **.NET 10 SDK** (`dotnet build` / `dotnet run`)
- **nvidia-smi** on PATH for VRAM readings (any recent NVIDIA driver on Windows/Linux).
  If missing, VRAM columns are `-1` — everything else still works.
- A running OpenAI-compatible server with a `/v1/chat/completions` endpoint.

## Quick start

```powershell
# 1. Put your Unsloth token in the environment (not in the config file):
setx UNSLOTH_API_KEY sk-unsloth-...      # or: set UNSLOTH_API_KEY=... for one shell

# 2. Run the default config (Unsloth on localhost:8888) from the CLI:
dotnet run

# 3. Or run against FreeToken Desktop (localhost:1919, no auth needed):
dotnet run -- config.freetoken.json

# 4. Or use the web dashboard — pick a config, hit Start, watch live progress,
#    and view charts of every past run:
dotnet run --project Dashboard     # → http://localhost:5274
```

Results land in `results/` with a timestamp suffix, e.g. `speed-20260824-221430.csv`.

## CLI

```
dotnet run -- [config-path] [--devui]
```

- **`config-path`** (optional): path to a config JSON. Defaults to `config.json` next to the exe.
- **`--devui`** (optional): start an interactive DevUI instead of the automated benchmark (see below).

## Configuration

### Bundled configs

| File | Target | Auth |
|---|---|---|
| `config.json` | Unsloth Desktop, `http://localhost:8888/v1` | `${UNSLOTH_API_KEY}` env var |
| `config.freetoken.json` | FreeToken Desktop, `http://127.0.0.1:1919/v1` | none (`"apiKey": ""`) |

### Fields

```jsonc
{
  "baseUrl": "http://localhost:8888/v1",
  "apiKey": "${UNSLOTH_API_KEY}",     // ${VAR_NAME} is substituted from the environment at startup
  "repeatsPerTask": 2,                // attempts per task in the speed/quality pass
  "sampling": {
    "temperature": 0.7,
    "topP": 0.8,
    "topK": 20,
    "minP": 0.05,
    "repetitionPenalty": 1.1,
    "enableThinking": false,          // off by default: reasoning models can otherwise burn
                                      // the whole max_tokens budget on chain-of-thought
    "reasoningEffort": "low"          // omit (null) on servers that don't support it
  },
  "models": [
    { "id": "model-id-or-repo:quant", "displayName": "Human-Readable Name" }
  ],
  "tasks": [
    {
      "name": "task-name",
      "systemPrompt": "optional",
      "prompt": "the user prompt",
      "maxTokens": 1000
    }
  ],
  "contextProbe": {
    "enabled": true,
    "tokenSteps": [4096, 8192, 16384, 32768],
    "fillerText": "sentence repeated to pad the prompt to ~4 chars/token"
  },
  "agentBenchmark": {
    "enabled": true,
    "modelId": "defaults to models[0].id",
    "concurrencyLoad": {
      "enabled": true,
      "agentCounts": [1, 2, 4],       // N identical agents at once, per level
      "repeatsPerLevel": 1,
      "instructions": "system prompt for the load agent",
      "prompt": "user prompt for the load agent",
      "maxTokens": 900
    },
    "workflow": {
      "enabled": true,
      "prompt": "input to the first stage",
      "maxTokensPerStage": 1800,
      "parallelPipelineCounts": [1, 2],
      "repeatsPerLevel": 1,
      "stages": [
        { "name": "Planner",  "instructions": "..." },
        { "name": "Coder",    "instructions": "..." },
        { "name": "Reviewer", "instructions": "..." }
      ]
    }
  }
}
```

**Secrets:** `apiKey` supports `${VAR_NAME}` placeholders, expanded from the environment at
startup. If the variable is unset, the tool prints a warning and continues unauthenticated
instead of sending the literal placeholder. Local servers that need no auth can just use
`"apiKey": ""`.

## Dashboard

A single-page web app that can **run** benchmarks (live stage + log, stop button) and
**view** results (charts over everything in `results/`). It reuses the exact same runner
and CSV format as the CLI — runs started from either side land in the same `results/`
folder and show up in both.

```powershell
dotnet run --project Dashboard    # standalone → http://localhost:5274
```

- **Port** is fixed at `http://localhost:5274`; override with the `DASHBOARD_URL` env var
  (e.g. `set DASHBOARD_URL=http://localhost:9000`).
- **Configs**: every `*.json` next to the dashboard exe shows up in the config dropdown
  (`config.json` / `config.freetoken.json` are copied there at build time). `${VAR}` env
  placeholders resolve from the dashboard process's environment — same rule as the CLI.
- **Results** are read from `<exe-dir>/results`; past CLI runs are visible immediately
  (the folder is copied into the output directory at build time).
- **Stop** cancels the run gracefully; whatever completed so far is written to `results/`
  with a normal timestamp, exactly like a finished run.

### Running via .NET Aspire

```powershell
dotnet run --project AppHost      # starts the dashboard on http://localhost:5274
```

The AppHost orchestrates the dashboard as its single resource (pinned to the same 5274
port, so the URL is identical in both modes). The LLM server itself is **not** a managed
resource — it's assumed already running. Env inheritance: child processes inherit the
AppHost's environment, so `UNSLOTH_API_KEY` set on the machine reaches the dashboard with
no explicit plumbing.

## DevUI mode

```
dotnet run -- --devui [config-path]
```

Hosts the same agents the benchmark uses — each workflow stage, the concurrency-load agent,
and the full sequential workflow — as individually chattable entities in Microsoft Agent
Framework's DevUI, pointed at the same server the benchmark measures. Open
**http://localhost:5273/devui** in a browser. Useful for poking at a single stage's behavior
by hand instead of only seeing aggregate CSV numbers.

## Output files (in `results/`)

| File | Contents |
|---|---|
| `speed-*.csv` | Per task+attempt: TTFT, total duration, prompt/completion tokens, tokens/s, VRAM before/after, errors |
| `context-probe-*.csv` | Per context step: requested tokens, device max context (from `/v1/models`), success, duration, VRAM |
| `quality-transcripts-*.txt` | Raw model outputs, for manual grading |
| `agent-concurrency-*.csv` | Per agent+level: TTFT, duration, tokens, tokens/s |
| `agent-concurrency-summary-*.csv` | Per level: wall-clock, success/fail counts, aggregate tokens/s, avg TTFT |
| `agent-workflow-stages-*.csv` | Per stage+pipeline: TTFT, duration, tokens, tokens/s |
| `agent-workflow-pipelines-*.csv` | Per pipeline: total duration, success, errors |

All files get a `yyyyMMdd-HHmmss` timestamp suffix.

## Notes & caveats

- **Tokens/s** is only reported when generation took ≥ 50 ms; below that, clock resolution
  makes the number meaningless, so the CSV shows `NaN`/blank instead of a garbage value.
- **Token counts** prefer the server's `usage` field; when absent they fall back to a
  ~4-chars-per-token estimate (fallback, not a substitute).
- **VRAM** reads the first GPU from `nvidia-smi`; adjust `GpuMonitor.cs` if you have multiple.
- **Context probe** steps that exceed the device max context are skipped (the server
  truncates rather than fails, so testing past the cap would just re-measure the capped size).
- **Model loading**: a `repo:quant` model id that isn't downloaded yet triggers an on-demand
  pull, so the warmup timeout is 60 minutes by default.

## Project layout

Four projects in one solution (`LlmBenchmark.slnx`):

```
Program.cs               CLI entry point: arg parse → runner → CSV write → summary
LlmBenchmark.csproj      root = the CLI exe (also hosts DevUI mode)

Core/                    LlmBenchmark.Core — shared library, namespace stays LlmBenchmark
  BenchmarkRunner.cs     the full benchmark loop (warmup, speed pass, context probe,
                         agent concurrency + workflow), stage callback, cancellation
  ConfigLoader.cs        config JSON load + ${VAR} env placeholder resolution
  OpenAiClient.cs        minimal streaming OpenAI-compatible client (TTFT capture)
  AgentFrameworkRunner.cs  Agent Framework concurrency load + Planner/Coder/Reviewer workflow
  GpuMonitor.cs          nvidia-smi VRAM reader
  TokenEstimator.cs      ~4 chars/token fallback estimator
  ResultsWriter.cs       CSV/transcript writers (shared by CLI and dashboard)
  Models/                config + result record types (incl. BenchmarkRunResult aggregate)

Dashboard/               LlmBenchmark.Dashboard — ASP.NET Core web app (port 5274)
  Program.cs             minimal API: /api/status, /api/run, /api/stop,
                         /api/configs, /api/results, /api/runs/{ts}[/transcripts]
  RunController.cs       singleton run state machine + capped live log buffer
  ResultsService.cs      reads results/*.csv (CsvHelper) into typed rows for the UI
  wwwroot/index.html     single-page dark UI: live run panel + Chart.js charts

AppHost/                 LlmBenchmark.AppHost — .NET Aspire 13 host; starts the dashboard

devui mode lives in DevUiHost.cs (root project) — it needs the Agent Framework
DevUI/Hosting packages, which are referenced there rather than in Core.
config.json              default config (Unsloth, key via UNSLOTH_API_KEY)
config.freetoken.json    FreeToken Desktop config (no auth)
```
