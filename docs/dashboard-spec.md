# Spec: Dashboard + Aspire orchestration

## Goal

Add a web dashboard that can **run** benchmarks (with live progress + stop) and **view** results
(charts over the `results/*.csv` files), orchestrated with **.NET Aspire 13**. The existing CLI
behavior stays byte-identical.

## Solution layout

```
LlmBenchmark.slnx
├── LlmBenchmark.csproj            (existing, root — console app; CLI + --devui modes unchanged)
│   ├── Program.cs                 (slimmed: arg parse → load config → devui | runner → write results)
│   └── DevUiHost.cs               (stays here)
├── Core/LlmBenchmark.Core.csproj  (new classlib — shared logic, namespace LlmBenchmark unchanged)
│   ├── OpenAiClient.cs            (moved from root)
│   ├── AgentFrameworkRunner.cs    (moved)
│   ├── GpuMonitor.cs              (moved)
│   ├── TokenEstimator.cs          (moved)
│   ├── ResultsWriter.cs           (moved + new WriteRun())
│   ├── BenchmarkRunner.cs         (NEW — benchmark loop extracted from Program.cs)
│   ├── ConfigLoader.cs            (NEW — config load + ${VAR} env resolution)
│   └── Models/                    (moved; + BenchmarkRunResult)
├── Dashboard/LlmBenchmark.Dashboard.csproj  (new web app, port 5274)
│   ├── Program.cs                 (WebApplication: static wwwroot + /api/* endpoints)
│   ├── RunController.cs           (singleton run state machine: start/stop/status/log buffer)
│   └── ResultsService.cs          (scan results/, group by timestamp, parse CSVs → JSON via CsvHelper)
│   └── wwwroot/index.html         (single-file dashboard UI, Chart.js 4 via jsDelivr CDN)
└── AppHost/LlmBenchmark.AppHost.csproj      (new Aspire 13 AppHost)
    └── Program.cs                 (AddProject<Projects.LlmBenchmark_Dashboard>("dashboard"))
```

- Root csproj: keep `Microsoft.NET.Sdk.Web` + Exe (DevUiHost needs ASP.NET).
- Agent Framework packages, split by consumer (deviation from the original "all five to
  Core" plan): **Core** gets `Microsoft.Agents.AI.OpenAI` + `Microsoft.Agents.AI.Workflows`
  (what `AgentFrameworkRunner` needs; flows transitively to CLI and Dashboard). The DevUI
  trio (`DevUI`, `Hosting`, `Hosting.OpenAI`) stays in the **root** csproj — its only
  consumer is `DevUiHost.cs`, and `Microsoft.Agents.AI.DevUI` ships its own
  `staticwebassets/index.html`, which collides with the Dashboard's `wwwroot/index.html`
  in the Static Web Assets manifest if it flows into the web app.
- All new code keeps namespace `LlmBenchmark` / `LlmBenchmark.Models` (except Dashboard's own
  API types in `LlmBenchmark.Dashboard`) so moved files need zero edits.
- Config files (`config*.json`) are copied to Dashboard output via csproj `<Content>` items,
  matching the existing "config next to the exe" convention. **All dashboard paths resolve
  against `AppContext.BaseDirectory`** (configs read from there, results written to
  `BaseDirectory/results`).

## Core changes

### `BenchmarkRunner` (new)

The entire benchmark loop currently inlined in `Program.cs` moves here, behavior preserved:

```csharp
public sealed class BenchmarkRunner
{
    public BenchmarkRunner(BenchmarkConfig config);
    public string CurrentStage { get; }   // e.g. "Qwen…: task codegen attempt 2/3"
    public Task<BenchmarkRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default);
}

public sealed class BenchmarkRunResult
{
    public List<SpeedResult> SpeedResults;
    public List<ContextProbeResult> ContextResults;
    public List<QualityRecord> QualityRecords;
    public List<AgentConcurrencyRunResult> AgentConcurrencyRuns;
    public List<AgentConcurrencyLevelSummary> AgentConcurrencySummaries;
    public List<AgentWorkflowStageResult> WorkflowStages;
    public List<AgentWorkflowPipelineSummary> WorkflowPipelines;
}
```

- `log` writes raw text **without** newline guarantees (same strings as today's console output:
  CLI passes `Console.Write`, dashboard appends to a buffer). CLI output stays identical.
- `CurrentStage` values: `Warming up {model}`, `{model}: task {task} attempt {i}/{n}`,
  `{model}: context probe ~{tokens} tokens`, `Agent benchmark: concurrency x{n}`,
  `Agent benchmark: workflow x{n}`, `Writing results`.
- **Cancellation**: `ct` is threaded into every client call (all already accept it). Warmup polls
  every 3 s with `Task.Delay(ct)`, so Stop takes effect within ~3 s even while warming up.
  On cancel, the runner catches `OperationCanceledException` at the top level, logs
  "Run cancelled — keeping partial results", and returns whatever was collected so far.
- **Results writing**: new `ResultsWriter.WriteRun(outDir, timestamp, run)` writes the same
  timestamped CSVs as today (`speed-`, `context-probe-`, `quality-transcripts-`, and the four
  agent files when present). Both CLI and dashboard call it, so dashboard runs land in `results/`
  exactly like CLI runs. Partial (cancelled) results are written too.

### `ConfigLoader` (new)

```csharp
public static class ConfigLoader
{
    public static BenchmarkConfig Load(string path, Action<string>? log = null);
}
```

Deserializes the JSON and resolves `${VAR_NAME}` in `apiKey` from the environment (today's
`ResolveEnvPlaceholder`, moved here). Warns via `log` when a referenced variable is unset.

### CLI (`Program.cs`)

Unchanged contract: `dotnet run -- [config-path] [--devui]`. Body becomes: parse args →
`ConfigLoader.Load` → devui mode or `new BenchmarkRunner(config).RunAsync(Console.Write)` →
`ResultsWriter.WriteRun("results", timestamp, results)` → same final summary lines as today.

## Dashboard app (port 5274)

`WebApplication`, static `wwwroot`, JSON API under `/api/*`. No auth (local tool). Port fixed at
`http://localhost:5274`, overridable via `DASHBOARD_URL` env var (DevUI keeps 5273).

### Run lifecycle

Single-run mutex, managed by a singleton `RunController`:

```
idle ──POST /api/run──▶ running(stage) ──▶ finished: completed | cancelled | failed
                          │
                          └── POST /api/stop (cts.Cancel()) ──▶ cancelled
```

- Starting a run loads the config, resolves the API key, and runs `BenchmarkRunner` on a
  background task; log lines append to a capped buffer (~200 KB tail); on completion/cancel it
  writes CSVs via `ResultsWriter.WriteRun` and records the outcome.
- **Error handling**: bad/missing config path → 400; unparseable config → 422 (run never starts);
  exception inside the run → outcome `failed` + `lastError` set, partial results still written.

### API

| Endpoint | Behavior |
|---|---|
| `GET /api/configs` | All `*.json` in `BaseDirectory` (SDK-generated `*.deps.json` / `*.runtimeconfig.json` / `staticwebassets*` excluded) → `[{ "name": "config.json", "path": "config.json" }]` |
| `GET /api/status` | `{ running, stage, configPath, startedAtUtc, finishedAtUtc, outcome, lastError, log }` — `log` is the raw text buffer (single string, console-faithful) |
| `POST /api/run` | Body `{ "configPath": "config.json" }` (default `config.json`). 202 on start, 409 if already running, 400/422 for config problems |
| `POST /api/stop` | Cancels the in-flight run. 200 `{ "stopping": true }`, 409 when idle |
| `GET /api/results` | Scans `results/`, groups files by their `yyyyMMdd-HHmmss` timestamp → `[{ "timestamp": "20260824-221430", "datasets": ["speed", "context-probe", …] }]`, newest first. Dataset keys: `speed`, `context-probe`, `quality-transcripts`, `agent-concurrency`, `agent-concurrency-summary`, `agent-workflow-stages`, `agent-workflow-pipelines` |
| `GET /api/runs/{timestamp}` | Parses that run's CSVs → object with one array per **present** dataset; each row is an object keyed by the CSV column header. Cells are typed: a column whose non-empty cells all parse as invariant-culture doubles becomes numbers (non-finite values — `NaN` from sub-50 ms generations — map to `null`, which the UI renders as "n/a"); `Success`/`Succeeded` columns become booleans; everything else strings |
| `GET /api/runs/{timestamp}/transcripts` | Parses the `=== ModelId :: TaskName :: attempt N ===` blocks → `[{ modelId, taskName, attempt, text }]` |

CSV parsing: **CsvHelper** (NuGet) handles the RFC-4180 field splitting — quoted fields,
`""` escapes, embedded commas/newlines in `Error` columns. Old CLI-produced files parse too,
so historical runs are viewable without re-running.

> CsvHelper 33.x quirk: `ReadHeader()` throws `ReaderException: No header record was found`
> on perfectly valid files (reproduced with `"a,b\n1,2"`). The service therefore reads the
> first record as the header via the core API (`Read()` + `ColumnCount` + `GetField(i)`) —
> verified working in 33.1.0.

### UI (`wwwroot/index.html`, single file, dark theme, Chart.js 4 from jsDelivr)

- **Header**: config `<select>` (from `/api/configs`), **Start** / **Stop** buttons, status pill
  (idle / running + stage / finished outcome). Polls `/api/status` every 1.5 s while running.
- **Live run**: current stage line + auto-scrolling `<pre>` of the log buffer; when a run
  finishes, refresh the runs list and auto-select the new run.
- **Runs table**: one row per timestamp with dataset chips; click loads that run (auto-selects
  the newest on page load).
- **Charts for the selected run** (each section hidden when its dataset is absent):
  - *Speed*: two grouped bar charts side by side — mean tokens/s and mean TTFT ms per
    `model · task` (successful attempts only; NaN tok/s excluded, noted in a caption).
  - *Context probe*: bar of max successful context per model + step table (✓/✗ per token step,
    device max context, VRAM after).
  - *Agent concurrency*: bars of mean aggregate tokens/s per concurrency level with avg TTFT as
    a line on a secondary axis; small table of wall clock + success/fail counts.
  - *Workflow*: bars of mean pipeline duration per parallel-pipeline count; table of mean
    per-stage durations (tokens/s where reliable).
  - *Transcripts*: expandable `model :: task :: attempt` entries with the raw text.

## AppHost (Aspire 13)

- Project SDK: `Aspire.AppHost.Sdk/13.5.2` (the modern replacement for the plain SDK +
  `Aspire.Hosting.AppHost` package, which is deprecated in Aspire 13).
- `DistributedApplication.CreateBuilder(args)` →
  `AddProject<Projects.LlmBenchmark_Dashboard>("dashboard").WithHttpEndpoint(port: 5274)`
  → `Run()`. The `Projects.*` class comes from the AppHost SDK's project-reference sourcegen.
- **Why the explicit endpoint**: recent Aspire versions no longer implicitly expose project
  resources — without `WithHttpEndpoint` the dashboard starts with no `ASPNETCORE_URLS`
  (it falls back to its own 5274, but Aspire can't link to it). Pinning 5274 explicitly keeps
  the URL identical in standalone and Aspire modes.
- **LLM server is NOT a managed resource** — Unsloth/FreeToken Desktop are assumed already
  running; the dashboard just points at whatever `baseUrl` its config says.
- Env inheritance: child processes inherit the AppHost's environment, so `UNSLOTH_API_KEY`
  set on the machine reaches the dashboard and resolves `${UNSLOTH_API_KEY}` as before. No
  explicit env plumbing needed (noted in README).
- Aspire Dashboard gives structured logs of the dashboard app for free.

## Test plan

1. `dotnet build` the whole solution — zero warnings-as-errors, CLI project still compiles with
   transitive Agent Framework references.
2. CLI regression: `dotnet run -- config.json` starts and produces identical console output up
   to the first network-dependent step (no live server needed to verify startup path).
3. Dashboard standalone (`dotnet run --project Dashboard`): `GET /api/results` lists the existing
   historical runs; `GET /api/runs/{ts}` returns typed rows for a real run; `/` serves the UI.
4. Run lifecycle without an LLM server: temp config pointing at `http://localhost:9/v1` →
   `POST /api/run` → status shows `running` + warmup stage → `POST /api/stop` → outcome
   `cancelled`, partial CSVs written, next run accepted.
5. AppHost: `dotnet run --project AppHost` starts the dashboard resource; Aspire Dashboard lists it.

## Out of scope (for now)

- Auth/multi-user, Docker packaging, managing the LLM server's lifecycle from Aspire.
- Cross-run comparison views (nice later: overlay two timestamps' charts).
- Automatic quality grading — transcripts stay manual, as today.
