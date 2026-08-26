using LlmBenchmark.Models;

namespace LlmBenchmark;

/// <summary>
/// The full benchmark loop, extracted from Program.cs so the CLI and the
/// dashboard run exactly the same code. Output goes through the <c>log</c>
/// callback as raw text with bare "\n" terminators (no newline guarantees —
/// a "..." prompt line is one call, its result line another), so the CLI can
/// pass Console.Write and the dashboard can append to a buffer. Cancellation
/// takes effect within one in-flight request plus the 3 s warmup poll; on
/// cancel the runner keeps whatever was collected so far.
/// </summary>
public sealed class BenchmarkRunner
{
    private const double MinReliableGenSeconds = 0.05;

    private readonly BenchmarkConfig _config;
    private readonly OpenAiClient _client;
    private Action<string>? _log;

    /// <summary>Human-readable description of the step currently in flight (for dashboards).
    /// Volatile: written on the run thread and polled from the HTTP status thread.</summary>
    public volatile string CurrentStage = "";

    public BenchmarkRunner(BenchmarkConfig config)
    {
        _config = config;
        _client = new OpenAiClient(config.BaseUrl, config.ApiKey);
    }

    public async Task<BenchmarkRunResult> RunAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        _log = log;
        var result = new BenchmarkRunResult();

        try
        {
            Log($"Base URL: {_config.BaseUrl}\n");
            Log($"Models to test: {string.Join(", ", _config.Models.Select(m => m.DisplayName))}\n\n");

            foreach (var model in _config.Models)
            {
                ct.ThrowIfCancellationRequested();
                CurrentStage = $"Warming up {model.DisplayName}";
                Log(new string('=', 70) + "\n");
                Log($"MODEL: {model.DisplayName}  (id: {model.Id})\n");
                Log(new string('=', 70) + "\n");
                Log("Requesting model (Unsloth will auto-load — and auto-download if needed — it)... ");

                // Long timeout: a "repo:quant" model id that isn't downloaded yet triggers an
                // on-demand pull (tens of GB), which can easily exceed a short warmup window.
                var (ready, loadSeconds, lastError) = await _client.WarmUpModelAsync(
                    model.Id,
                    timeout: TimeSpan.FromMinutes(60),
                    _config.Sampling,
                    onProgress: msg => Log($"\n  {msg}"),
                    ct);

                if (!ready)
                {
                    Log("\n");
                    Log($"  Could not get a response from '{model.Id}' after {loadSeconds:F0}s: {lastError}\n");
                    Log("  Check the model id matches what Unsloth expects, then skipping this model.\n\n");
                    continue;
                }

                Log($"ready after {loadSeconds:F0}s.\n");

                var (vramBeforeAny, vramTotal) = GpuMonitor.ReadVram();
                Log($"VRAM after load: {vramBeforeAny} MB / {vramTotal} MB\n\n");

                // ---- Speed + quality pass over the fixed task set ----
                foreach (var task in _config.Tasks)
                {
                    for (int attempt = 1; attempt <= _config.RepeatsPerTask; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        CurrentStage = $"{model.DisplayName}: task {task.Name} attempt {attempt}/{_config.RepeatsPerTask}";
                        Log($"  [{task.Name}] attempt {attempt}/{_config.RepeatsPerTask}... ");

                        var (vramBefore, _) = GpuMonitor.ReadVram();
                        var chatResult = await _client.StreamChatCompletionAsync(
                            model.Id, task.SystemPrompt, task.Prompt, task.MaxTokens, _config.Sampling, ct);
                        var (vramAfter, _) = GpuMonitor.ReadVram();

                        if (!chatResult.Success)
                        {
                            Log($"FAILED: {chatResult.Error}\n");
                            result.SpeedResults.Add(new SpeedResult
                            {
                                ModelId = model.Id,
                                TaskName = task.Name,
                                Attempt = attempt,
                                Success = false,
                                Error = chatResult.Error,
                                VramUsedMbBefore = vramBefore,
                                VramUsedMbAfter = vramAfter
                            });
                            continue;
                        }

                        int promptTok = chatResult.PromptTokens ?? TokenEstimator.EstimateTokens(task.Prompt);
                        int completionTok = chatResult.CompletionTokens ?? TokenEstimator.EstimateTokens(chatResult.FullText);
                        double genSeconds = (chatResult.TotalDurationMs - chatResult.TtftMs) / 1000.0;
                        // Below this, clock resolution / the "no content chunk arrived" fallback
                        // (TtftMs forced equal to TotalDurationMs) makes the duration meaningless
                        // as a denominator — reporting a number here would just be noise dressed
                        // up as precision (we used to floor this to 0.001s, which produced
                        // "400000 tok/s" style garbage).
                        double tokPerSec = genSeconds >= MinReliableGenSeconds
                            ? completionTok / genSeconds
                            : double.NaN;

                        string reasoningNote = chatResult.AnswerIsReasoningOnly ? " [reasoning-only, no final content]" : "";
                        string tokRateDisplay = double.IsNaN(tokPerSec) ? "n/a" : $"{tokPerSec:F1}";
                        Log($"TTFT {chatResult.TtftMs:F0}ms, {tokRateDisplay} tok/s, " +
                             $"{completionTok} tokens, VRAM {vramAfter}MB{reasoningNote}\n");

                        result.SpeedResults.Add(new SpeedResult
                        {
                            ModelId = model.Id,
                            TaskName = task.Name,
                            Attempt = attempt,
                            Success = true,
                            TtftMs = chatResult.TtftMs,
                            TotalDurationMs = chatResult.TotalDurationMs,
                            PromptTokensEstimate = promptTok,
                            CompletionTokensEstimate = completionTok,
                            TokensPerSecond = tokPerSec,
                            VramUsedMbBefore = vramBefore,
                            VramUsedMbAfter = vramAfter
                        });

                        result.QualityRecords.Add(new QualityRecord
                        {
                            ModelId = model.Id,
                            TaskName = task.Name,
                            Attempt = attempt,
                            ResponseText = chatResult.FullText
                        });
                    }
                }

                // ---- Context length probe ----
                if (_config.ContextProbe.Enabled)
                {
                    ct.ThrowIfCancellationRequested();
                    int? deviceMaxContext = await _client.GetModelContextLimitAsync(model.Id, ct);

                    Log("\n");
                    if (deviceMaxContext.HasValue)
                    {
                        Log($"  Device max context (Unsloth auto-fit for this GPU): {deviceMaxContext} tokens\n");
                    }
                    else
                    {
                        Log("  Could not read device max context from /v1/models — probe steps beyond " +
                             "actual capacity may be silently truncated by the server rather than failing.\n");
                    }
                    Log("  Context probe:\n");

                    foreach (int targetTokens in _config.ContextProbe.TokenSteps)
                    {
                        ct.ThrowIfCancellationRequested();
                        CurrentStage = $"{model.DisplayName}: context probe ~{targetTokens} tokens";
                        if (deviceMaxContext.HasValue && targetTokens > deviceMaxContext.Value)
                        {
                            Log($"    ~{targetTokens} tokens... SKIPPED (exceeds device max context " +
                                 $"{deviceMaxContext}; the server truncates rather than fails, so testing " +
                                 "it would just re-measure the capped size under a bigger label)\n");
                            result.ContextResults.Add(new ContextProbeResult
                            {
                                ModelId = model.Id,
                                RequestedContextTokens = targetTokens,
                                DeviceMaxContextTokens = deviceMaxContext,
                                Succeeded = false,
                                TotalDurationMs = 0,
                                VramUsedMbAfter = 0,
                                Error = $"Skipped — exceeds device max context ({deviceMaxContext} tokens)"
                            });
                            continue;
                        }

                        string filler = BuildFillerText(_config.ContextProbe.FillerText, targetTokens);
                        string probePrompt = filler + "\n\nSummarize the above in one sentence.";

                        Log($"    ~{targetTokens} tokens... ");
                        var (_, _) = GpuMonitor.ReadVram();

                        var probeResult = await _client.StreamChatCompletionAsync(
                            model.Id, null, probePrompt, maxTokens: 100, _config.Sampling, ct);

                        var (vramAfterProbe, _) = GpuMonitor.ReadVram();

                        if (probeResult.Success)
                        {
                            Log($"OK ({probeResult.TotalDurationMs:F0}ms, VRAM {vramAfterProbe}MB)\n");
                        }
                        else
                        {
                            Log($"FAILED: {probeResult.Error}\n");
                        }

                        result.ContextResults.Add(new ContextProbeResult
                        {
                            ModelId = model.Id,
                            RequestedContextTokens = targetTokens,
                            DeviceMaxContextTokens = deviceMaxContext,
                            Succeeded = probeResult.Success,
                            TotalDurationMs = probeResult.TotalDurationMs,
                            VramUsedMbAfter = vramAfterProbe,
                            Error = probeResult.Error
                        });

                        // Stop climbing once it fails — higher steps will fail too.
                        if (!probeResult.Success) break;
                    }
                }

                Log("\n");
            }

            if (_config.AgentBenchmark.Enabled)
            {
                ct.ThrowIfCancellationRequested();
                string agentModelId = string.IsNullOrWhiteSpace(_config.AgentBenchmark.ModelId)
                    ? _config.Models.FirstOrDefault()?.Id ?? ""
                    : _config.AgentBenchmark.ModelId;

                Log(new string('=', 70) + "\n");
                Log($"MICROSOFT AGENT FRAMEWORK BENCHMARK  (model: {agentModelId})\n");
                Log(new string('=', 70) + "\n");

                if (string.IsNullOrWhiteSpace(agentModelId))
                {
                    Log("  No model configured for agentBenchmark and no fallback in models[] — skipping.\n");
                }
                else
                {
                    var agentRunner = new AgentFrameworkRunner(_config.BaseUrl, _config.ApiKey, agentModelId);

                    if (_config.AgentBenchmark.ConcurrencyLoad.Enabled)
                    {
                        ct.ThrowIfCancellationRequested();
                        Log("Concurrency load (N identical agents hitting the server at once):\n");
                        var (runs, summaries) = await agentRunner.RunConcurrencyLoadAsync(
                            _config.AgentBenchmark.ConcurrencyLoad,
                            msg => Log(msg),
                            onStage: stage => CurrentStage = stage,
                            ct);
                        result.AgentConcurrencyRuns.AddRange(runs);
                        result.AgentConcurrencySummaries.AddRange(summaries);
                    }

                    if (_config.AgentBenchmark.Workflow.Enabled)
                    {
                        ct.ThrowIfCancellationRequested();
                        Log("Multi-agent workflow (sequential pipeline, optionally run concurrently):\n");
                        var (stages, pipelines) = await agentRunner.RunWorkflowAsync(
                            _config.AgentBenchmark.Workflow,
                            msg => Log(msg),
                            onStage: stage => CurrentStage = stage,
                            ct);
                        result.WorkflowStages.AddRange(stages);
                        result.WorkflowPipelines.AddRange(pipelines);
                    }
                }

                Log("\n");
            }

            // The caller (CLI / dashboard) writes the CSVs next — this label is
            // what a status poll shows in that brief window.
            CurrentStage = "Writing results";
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Log("Run cancelled — keeping partial results\n");
            return result;
        }
    }

    private void Log(string text) => _log?.Invoke(text);

    private static string BuildFillerText(string sentence, int targetTokens)
    {
        // Each step used to be built by repeating the same sentence, which makes the
        // 8K-token prompt a literal byte-prefix of the 16K prompt, which is a prefix
        // of the 32K prompt, and so on. Backends that do prompt-prefix KV-cache reuse
        // (common on llama.cpp/Unsloth-style servers) then only pay for the small
        // incremental tail on each larger step instead of the full context, making
        // bigger context sizes look artificially — even impossibly — fast. A unique
        // nonce up front shifts every token after it, so each probe step is a genuine
        // cache miss and actually measures prefill at that context size.
        string nonce = $"[probe-{Guid.NewGuid():N}] ";

        // ~4 chars/token heuristic to pad the prompt to roughly the target size.
        int targetChars = targetTokens * 4;
        var sb = new System.Text.StringBuilder(targetChars + sentence.Length + nonce.Length);
        sb.Append(nonce);
        while (sb.Length < targetChars) sb.Append(sentence);
        return sb.ToString();
    }
}
