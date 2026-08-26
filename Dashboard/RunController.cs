using System.Text;
using LlmBenchmark.Models;

namespace LlmBenchmark.Dashboard;

/// <summary>
/// Singleton run state machine: at most one benchmark run at a time.
///
///   idle ──POST /api/run──▶ running(stage) ──▶ completed | cancelled | failed
///                             │
///                             └── POST /api/stop (cts.Cancel()) ──▶ cancelled
///
/// The runner's log lines append to a capped tail buffer (~200 KB) that
/// /api/status returns as one raw string, console-faithful. On completion,
/// cancellation, or failure the collected (possibly partial) results are
/// written via ResultsWriter.WriteRun into results/ next to the exe — exactly
/// like CLI runs.
/// </summary>
public sealed class RunController
{
    private const int MaxLogChars = 200_000;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private BenchmarkRunner? _runner;
    private Task? _runTask;
    private readonly StringBuilder _log = new();

    public bool Running { get; private set; }
    public string? ConfigPath { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? FinishedAtUtc { get; private set; }
    public string? Outcome { get; private set; } // completed | cancelled | failed
    public string? LastError { get; private set; }

    /// <summary>Starts a run. Returns false with an error message when one is already in flight.</summary>
    /// <param name="preLog">Text to seed the log buffer with (e.g. config-load warnings) before the runner emits anything.</param>
    /// <param name="overrides">Optional UI overrides applied to <paramref name="config"> before the run starts.</param>
    public bool TryStart(BenchmarkConfig config, string configPath, string preLog, RunOverrides? overrides, out string? error)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (Running)
            {
                error = "A benchmark run is already in progress.";
                return false;
            }
            ApplyOverrides(config, overrides);
            cts = _cts = new CancellationTokenSource();
            _runner = new BenchmarkRunner(config);
            ConfigPath = configPath;
            StartedAtUtc = DateTime.UtcNow;
            FinishedAtUtc = null;
            Outcome = null;
            LastError = null;
            lock (_log)
            {
                _log.Clear();
                _log.Append(preLog);
            }
            Running = true;
            error = null;
        }
        _runTask = Task.Run(() => RunLoopAsync(config, cts.Token));
        return true;
    }

    public bool TryStop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!Running || _cts is null) return false;
            cts = _cts;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished between the check and the cancel — treat as no-op.
        }
        return true;
    }

    /// <summary>
    /// Applies UI-provided overrides onto the loaded config in place. Only fields
    /// that carry a value change anything; a null/blank field leaves the config
    /// value from the file untouched. Numeric knobs are guarded with > 0 so that
    /// "leave alone" (0 or empty) never clobbers a legitimate 0 (e.g. greedy
    /// generation) — but such values aren't reachable from the UI anyway.
    /// Thinking stays off unless the checkbox is ticked (there's no "off" from the
    /// UI, and the config default is off anyway); reasoning effort is sent only
    /// when a tier is chosen.
    /// Context-probe and agent toggles are tri-state (not null) so they can turn a
    /// mode off as well as on. Token steps arrive as a comma/space-separated string
    /// and are parsed into ints (non-positive/non-numeric entries are dropped);
    /// the config list is left untouched if nothing parses.
    /// </summary>
    private static void ApplyOverrides(BenchmarkConfig config, RunOverrides? o)
    {
        if (o is null) return;

        if (!string.IsNullOrWhiteSpace(o.BaseUrl)) config.BaseUrl = o.BaseUrl;
        if (o.ApiKey is not null) config.ApiKey = o.ApiKey;
        if (o.RepeatsPerTask is > 0) config.RepeatsPerTask = o.RepeatsPerTask.Value;
        if (o.MaxTokens is > 0)
            foreach (var task in config.Tasks) task.MaxTokens = o.MaxTokens.Value;
        if (o.Temperature is > 0) config.Sampling.Temperature = o.Temperature.Value;
        if (o.TopP is > 0) config.Sampling.TopP = o.TopP.Value;
        if (o.TopK is > 0) config.Sampling.TopK = o.TopK.Value;
        if (o.MinP is > 0) config.Sampling.MinP = o.MinP.Value;
        if (o.RepetitionPenalty is > 0) config.Sampling.RepetitionPenalty = o.RepetitionPenalty.Value;
        if (o.EnableThinking is true) config.Sampling.EnableThinking = true;
        if (!string.IsNullOrWhiteSpace(o.ReasoningEffort)) config.Sampling.ReasoningEffort = o.ReasoningEffort;
        if (o.ContextProbeEnabled is not null) config.ContextProbe.Enabled = o.ContextProbeEnabled.Value;
        if (!string.IsNullOrWhiteSpace(o.ContextProbeFillerText)) config.ContextProbe.FillerText = o.ContextProbeFillerText;
        if (!string.IsNullOrWhiteSpace(o.ContextProbeTokenSteps))
        {
            var steps = new List<int>();
            foreach (var part in o.ContextProbeTokenSteps.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                if (int.TryParse(part, out var v) && v > 0) steps.Add(v);
            if (steps.Count > 0) config.ContextProbe.TokenSteps = steps;
        }
        if (o.AgentBenchmarkEnabled is not null) config.AgentBenchmark.Enabled = o.AgentBenchmarkEnabled.Value;
        if (!string.IsNullOrWhiteSpace(o.AgentBenchmarkModelId)) config.AgentBenchmark.ModelId = o.AgentBenchmarkModelId;
    }

    public object Status()
    {
        lock (_gate)
        {
            return new
            {
                running = Running,
                stage = Running ? _runner?.CurrentStage ?? "" : "",
                configPath = ConfigPath,
                startedAtUtc = StartedAtUtc?.ToString("o"),
                finishedAtUtc = FinishedAtUtc?.ToString("o"),
                outcome = Outcome,
                lastError = LastError,
                log = _log.ToString()
            };
        }
    }

    private async Task RunLoopAsync(BenchmarkConfig config, CancellationToken ct)
    {
        try
        {
            BenchmarkRunResult results = await _runner!.RunAsync(s => AppendLog(s), ct);

            // Partial (cancelled) results are written too. The timestamp is local
            // time, matching the CLI's filenames and the dashboard's own scans.
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            ResultsWriter.WriteRun(ResultsService.ResultsDir, timestamp, results);

            lock (_gate)
            {
                Outcome = ct.IsCancellationRequested ? "cancelled" : "completed";
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Run failed: {ex}\n");
            lock (_gate)
            {
                Outcome = "failed";
                LastError = ex.Message;
            }
        }
        finally
        {
            lock (_gate)
            {
                FinishedAtUtc = DateTime.UtcNow;
                Running = false;
                _runner = null;
            }
        }
    }

    private void AppendLog(string text)
    {
        lock (_log)
        {
            _log.Append(text);
            if (_log.Length > MaxLogChars)
                _log.Remove(0, _log.Length - MaxLogChars);
        }
    }
}
