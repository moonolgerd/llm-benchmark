using System.Text.Json;
using LlmBenchmark;
using LlmBenchmark.Models;

bool devUiMode = args.Contains("--devui");
string configPath = args.FirstOrDefault(a => a != "--devui") ?? "config.json";
if (!File.Exists(configPath))
{
    Console.WriteLine($"Config file not found: {configPath}");
    Console.WriteLine("Pass a path as the first argument, or place config.json next to the exe.");
    return 1;
}

var config = JsonSerializer.Deserialize<BenchmarkConfig>(
    File.ReadAllText(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? throw new InvalidOperationException("Failed to parse config.json");

// Secrets stay out of the config file: an apiKey of the form ${VAR_NAME} is
// resolved from the environment at startup (e.g. ${UNSLOTH_API_KEY}).
config.ApiKey = ResolveEnvPlaceholder(config.ApiKey);

if (devUiMode)
{
    await DevUiHost.RunAsync(config);
    return 0;
}

var client = new OpenAiClient(config.BaseUrl, config.ApiKey);

var speedResults = new List<SpeedResult>();
var contextResults = new List<ContextProbeResult>();
var qualityRecords = new List<QualityRecord>();

Console.WriteLine($"Base URL: {config.BaseUrl}");
Console.WriteLine($"Models to test: {string.Join(", ", config.Models.Select(m => m.DisplayName))}");
Console.WriteLine();

foreach (var model in config.Models)
{
    Console.WriteLine(new string('=', 70));
    Console.WriteLine($"MODEL: {model.DisplayName}  (id: {model.Id})");
    Console.WriteLine(new string('=', 70));
    Console.Write("Requesting model (Unsloth will auto-load — and auto-download if needed — it)... ");

    // Long timeout: a "repo:quant" model id that isn't downloaded yet triggers an
    // on-demand pull (tens of GB), which can easily exceed a short warmup window.
    var (ready, loadSeconds, lastError) = await client.WarmUpModelAsync(
        model.Id,
        timeout: TimeSpan.FromMinutes(60),
        config.Sampling,
        onProgress: msg => Console.Write($"\n  {msg}"));

    if (!ready)
    {
        Console.WriteLine();
        Console.WriteLine($"  Could not get a response from '{model.Id}' after {loadSeconds:F0}s: {lastError}");
        Console.WriteLine("  Check the model id matches what Unsloth expects, then skipping this model.");
        Console.WriteLine();
        continue;
    }

    Console.WriteLine($"ready after {loadSeconds:F0}s.");

    var (vramBeforeAny, vramTotal) = GpuMonitor.ReadVram();
    Console.WriteLine($"VRAM after load: {vramBeforeAny} MB / {vramTotal} MB");
    Console.WriteLine();

    // ---- Speed + quality pass over the fixed task set ----
    foreach (var task in config.Tasks)
    {
        for (int attempt = 1; attempt <= config.RepeatsPerTask; attempt++)
        {
            Console.Write($"  [{task.Name}] attempt {attempt}/{config.RepeatsPerTask}... ");

            var (vramBefore, _) = GpuMonitor.ReadVram();
            var chatResult = await client.StreamChatCompletionAsync(
                model.Id, task.SystemPrompt, task.Prompt, task.MaxTokens, config.Sampling);
            var (vramAfter, _) = GpuMonitor.ReadVram();

            if (!chatResult.Success)
            {
                Console.WriteLine($"FAILED: {chatResult.Error}");
                speedResults.Add(new SpeedResult
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
            const double MinReliableGenSeconds = 0.05;
            double tokPerSec = genSeconds >= MinReliableGenSeconds
                ? completionTok / genSeconds
                : double.NaN;

            string reasoningNote = chatResult.AnswerIsReasoningOnly ? " [reasoning-only, no final content]" : "";
            string tokRateDisplay = double.IsNaN(tokPerSec) ? "n/a" : $"{tokPerSec:F1}";
            Console.WriteLine($"TTFT {chatResult.TtftMs:F0}ms, {tokRateDisplay} tok/s, " +
                               $"{completionTok} tokens, VRAM {vramAfter}MB{reasoningNote}");

            speedResults.Add(new SpeedResult
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

            qualityRecords.Add(new QualityRecord
            {
                ModelId = model.Id,
                TaskName = task.Name,
                Attempt = attempt,
                ResponseText = chatResult.FullText
            });
        }
    }

    // ---- Context length probe ----
    if (config.ContextProbe.Enabled)
    {
        int? deviceMaxContext = await client.GetModelContextLimitAsync(model.Id);

        Console.WriteLine();
        if (deviceMaxContext.HasValue)
        {
            Console.WriteLine($"  Device max context (Unsloth auto-fit for this GPU): {deviceMaxContext} tokens");
        }
        else
        {
            Console.WriteLine("  Could not read device max context from /v1/models — probe steps beyond " +
                               "actual capacity may be silently truncated by the server rather than failing.");
        }
        Console.WriteLine("  Context probe:");

        foreach (int targetTokens in config.ContextProbe.TokenSteps)
        {
            if (deviceMaxContext.HasValue && targetTokens > deviceMaxContext.Value)
            {
                Console.WriteLine($"    ~{targetTokens} tokens... SKIPPED (exceeds device max context " +
                                   $"{deviceMaxContext}; the server truncates rather than fails, so testing " +
                                   "it would just re-measure the capped size under a bigger label)");
                contextResults.Add(new ContextProbeResult
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

            string filler = BuildFillerText(config.ContextProbe.FillerText, targetTokens);
            string probePrompt = filler + "\n\nSummarize the above in one sentence.";

            Console.Write($"    ~{targetTokens} tokens... ");
            var (_, _) = GpuMonitor.ReadVram();

            var probeResult = await client.StreamChatCompletionAsync(
                model.Id, null, probePrompt, maxTokens: 100, config.Sampling);

            var (vramAfterProbe, _) = GpuMonitor.ReadVram();

            if (probeResult.Success)
            {
                Console.WriteLine($"OK ({probeResult.TotalDurationMs:F0}ms, VRAM {vramAfterProbe}MB)");
            }
            else
            {
                Console.WriteLine($"FAILED: {probeResult.Error}");
            }

            contextResults.Add(new ContextProbeResult
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

    Console.WriteLine();
}

var agentConcurrencyRuns = new List<AgentConcurrencyRunResult>();
var agentConcurrencySummaries = new List<AgentConcurrencyLevelSummary>();
var agentWorkflowStages = new List<AgentWorkflowStageResult>();
var agentWorkflowPipelines = new List<AgentWorkflowPipelineSummary>();

if (config.AgentBenchmark.Enabled)
{
    string agentModelId = string.IsNullOrWhiteSpace(config.AgentBenchmark.ModelId)
        ? config.Models.FirstOrDefault()?.Id ?? ""
        : config.AgentBenchmark.ModelId;

    Console.WriteLine(new string('=', 70));
    Console.WriteLine($"MICROSOFT AGENT FRAMEWORK BENCHMARK  (model: {agentModelId})");
    Console.WriteLine(new string('=', 70));

    if (string.IsNullOrWhiteSpace(agentModelId))
    {
        Console.WriteLine("  No model configured for agentBenchmark and no fallback in models[] — skipping.");
    }
    else
    {
        var agentRunner = new AgentFrameworkRunner(config.BaseUrl, config.ApiKey, agentModelId);

        if (config.AgentBenchmark.ConcurrencyLoad.Enabled)
        {
            Console.WriteLine("Concurrency load (N identical agents hitting the server at once):");
            var (runs, summaries) = await agentRunner.RunConcurrencyLoadAsync(
                config.AgentBenchmark.ConcurrencyLoad, msg => Console.Write(msg));
            agentConcurrencyRuns.AddRange(runs);
            agentConcurrencySummaries.AddRange(summaries);
        }

        if (config.AgentBenchmark.Workflow.Enabled)
        {
            Console.WriteLine("Multi-agent workflow (sequential pipeline, optionally run concurrently):");
            var (stages, pipelines) = await agentRunner.RunWorkflowAsync(
                config.AgentBenchmark.Workflow, msg => Console.Write(msg));
            agentWorkflowStages.AddRange(stages);
            agentWorkflowPipelines.AddRange(pipelines);
        }
    }

    Console.WriteLine();
}

string outDir = "results";
Directory.CreateDirectory(outDir);
string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

ResultsWriter.WriteSpeedCsv(Path.Combine(outDir, $"speed-{timestamp}.csv"), speedResults);
ResultsWriter.WriteContextProbeCsv(Path.Combine(outDir, $"context-probe-{timestamp}.csv"), contextResults);
ResultsWriter.WriteQualityTranscripts(Path.Combine(outDir, $"quality-transcripts-{timestamp}.txt"), qualityRecords);

if (config.AgentBenchmark.Enabled)
{
    ResultsWriter.WriteAgentConcurrencyCsv(Path.Combine(outDir, $"agent-concurrency-{timestamp}.csv"), agentConcurrencyRuns);
    ResultsWriter.WriteAgentConcurrencySummaryCsv(Path.Combine(outDir, $"agent-concurrency-summary-{timestamp}.csv"), agentConcurrencySummaries);
    ResultsWriter.WriteAgentWorkflowStagesCsv(Path.Combine(outDir, $"agent-workflow-stages-{timestamp}.csv"), agentWorkflowStages);
    ResultsWriter.WriteAgentWorkflowPipelinesCsv(Path.Combine(outDir, $"agent-workflow-pipelines-{timestamp}.csv"), agentWorkflowPipelines);
}

Console.WriteLine();
Console.WriteLine($"Done. Results written to {Path.GetFullPath(outDir)}");
Console.WriteLine("  - speed-*.csv                    : TTFT / tok-s / VRAM per task+attempt");
Console.WriteLine("  - context-probe-*.csv            : max usable context before failure/offload");
Console.WriteLine("  - quality-transcripts-*.txt      : raw outputs for you to grade by hand");
if (config.AgentBenchmark.Enabled)
{
    Console.WriteLine("  - agent-concurrency-*.csv        : per-agent TTFT/tok-s under N-way concurrent load (Agent Framework)");
    Console.WriteLine("  - agent-concurrency-summary-*.csv: aggregate throughput per concurrency level");
    Console.WriteLine("  - agent-workflow-stages-*.csv    : per-stage timing within the multi-agent pipeline");
    Console.WriteLine("  - agent-workflow-pipelines-*.csv : whole-pipeline duration per concurrent run");
}

return 0;

static string ResolveEnvPlaceholder(string value)
{
    if (value.Length >= 3 && value.StartsWith("${") && value.EndsWith("}"))
    {
        string varName = value[2..^1];
        string? resolved = Environment.GetEnvironmentVariable(varName);
        if (string.IsNullOrEmpty(resolved))
        {
            Console.WriteLine($"Warning: apiKey placeholder ${{{varName}}} — environment variable '{varName}' is not set; requests will go out unauthenticated.");
        }
        return resolved ?? "";
    }
    return value;
}

static string BuildFillerText(string sentence, int targetTokens)
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
