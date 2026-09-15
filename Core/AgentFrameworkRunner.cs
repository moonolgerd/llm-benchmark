using System.ClientModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using LlmBenchmark.Models;
using OpenAI;
using OpenAI.Chat;

namespace LlmBenchmark;

/// <summary>
/// Runs the same local OpenAI-compatible server (Unsloth) through Microsoft
/// Agent Framework instead of the raw-HTTP OpenAiClient, to see how it behaves
/// under multi-agent load: many identical agents firing concurrently, and a
/// multi-stage agent pipeline (optionally itself run several times in parallel).
/// Below MinReliableGenSeconds a duration is too small to be a trustworthy
/// tok/s denominator, matching OpenAiClient's own threshold.
/// </summary>
public class AgentFrameworkRunner
{
    private const double MinReliableGenSeconds = 0.05;

    private readonly IChatClient _chatClient;

    public AgentFrameworkRunner(string baseUrl, string apiKey, string modelId)
    {
        _chatClient = CreateChatClient(baseUrl, apiKey, modelId);
    }

    // Shared with DevUiHost (separate assembly) so the interactive DevUI session
    // talks to the same local server the same way the benchmark does.
    public static IChatClient CreateChatClient(string baseUrl, string apiKey, string modelId)
    {
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var openAiClient = new OpenAIClient(credential, options);
        IChatClient chatClient = openAiClient.GetChatClient(modelId).AsIChatClient();

        // Only wrap when telemetry is actually enabled (see AgentTelemetry) — this
        // is a local benchmark tool talking to a local server, so surfacing full
        // prompt/response text in spans (EnableSensitiveData) is a deliberate
        // choice, not one that would be appropriate against a hosted provider.
        if (AgentTelemetry.IsEnabled)
        {
            chatClient = chatClient.AsBuilder()
                .UseOpenTelemetry(sourceName: AgentTelemetry.ChatClientSourceName,
                    configure: cfg => cfg.EnableSensitiveData = true)
                .Build();
        }

        return chatClient;
    }

    // Baked into the agent's own ChatOptions (rather than passed per-call) so it
    // applies uniformly whether the agent is run directly or driven indirectly
    // through AgentWorkflowBuilder, which gives callers no per-run options hook.
    // Deliberately unnamed (Name left null): AgentWorkflowBuilder.BuildSequential
    // forwards each agent's response — with its AuthorName, which ChatClientAgent
    // sets from .Name — into the next agent's request messages, and this local
    // server's stricter OpenAI-compatible validation (vLLM/Unsloth) 400s on
    // "name" appearing on a non-tool message. .Id is safe to set, though: the
    // workflow graph's executor label (GetDescriptiveId) falls back to .Id only
    // when .Name is empty, and .Id never touches AuthorName — so passing id
    // here gives readable graph/DevUI labels without reintroducing the bug.
    private ChatClientAgent MakeAgent(string? instructions, int maxTokens, string? id = null) =>
        new(_chatClient, new ChatClientAgentOptions
        {
            Id = id,
            ChatOptions = BuildChatOptions(instructions, maxTokens)
        });

    // enable_thinking is Unsloth/vLLM-specific, not part of the standard OpenAI
    // chat-completions schema, so Microsoft.Extensions.AI's ChatOptions has no
    // dedicated property for it. Without this, Qwen3's reasoning block can eat
    // the whole max-tokens budget and leave the visible .Text empty (matching
    // the "reasoning-only" case OpenAiClient.cs already special-cases for the
    // raw-HTTP path) — the JsonPatch escape hatch reaches the raw request body.
    public static ChatOptions BuildChatOptions(string? instructions, int maxTokens) => new()
    {
        Instructions = instructions,
        MaxOutputTokens = maxTokens,
        RawRepresentationFactory = _ =>
        {
            var raw = new ChatCompletionOptions();
#pragma warning disable SCME0001 // JsonPatch is the documented escape hatch for provider-specific request fields; stable enough for a benchmark tool.
            raw.Patch.Set("$.enable_thinking"u8, false);
#pragma warning restore SCME0001
            return raw;
        }
    };

    // ---------------------------------------------------------------------
    // Concurrency load: N identical agents run the same prompt at once.
    // ---------------------------------------------------------------------

    /// <param name="onStage">Optional hook reporting the sub-stage currently in flight
    /// (e.g. "Agent benchmark: concurrency x4") so callers can surface it in a UI.</param>
    public async Task<(List<AgentConcurrencyRunResult> Runs, List<AgentConcurrencyLevelSummary> Summaries)>
        RunConcurrencyLoadAsync(ConcurrencyLoadConfig cfg, Action<string> onProgress,
            Action<string>? onStage = null, CancellationToken ct = default)
    {
        var runs = new List<AgentConcurrencyRunResult>();
        var summaries = new List<AgentConcurrencyLevelSummary>();

        foreach (int level in cfg.AgentCounts)
        {
            onStage?.Invoke($"Agent benchmark: concurrency x{level}");
            for (int repeat = 1; repeat <= cfg.RepeatsPerLevel; repeat++)
            {
                onProgress($"  Concurrency x{level}, repeat {repeat}/{cfg.RepeatsPerLevel}... ");

                var agents = Enumerable.Range(0, level)
                    .Select(_ => MakeAgent(cfg.Instructions, cfg.MaxTokens))
                    .ToList();

                // This runs inside a fire-and-forget Task.Run off an ASP.NET Core
                // request handler (the dashboard's /api/run), so Activity.Current
                // would otherwise ambiently be that request's own — unsampled —
                // activity: OTel's default ParentBased sampler follows a parent's
                // sampling decision (an explicit-but-default ActivityContext doesn't
                // escape this either — it reads as "unsampled parent", not "no
                // parent"), so StartActivity would silently return null. Clearing
                // Activity.Current first forces this onto its own sampled root trace.
                Activity.Current = null;
                using Activity? batchActivity = AgentTelemetry.ActivitySource.StartActivity("agent-concurrency.batch");
                batchActivity?.SetTag("concurrency.level", level).SetTag("concurrency.repeat", repeat);

                var sw = Stopwatch.StartNew();
                var tasks = agents.Select((agent, i) => RunSingleAgentAsync(agent, cfg.Prompt, level, repeat, i, ct)
                    .ContinueWith(t =>
                    {
                        var r = t.Result;
                        r.ConcurrencyLevel = level;
                        r.Repeat = repeat;
                        r.AgentIndex = i;
                        return r;
                    }, ct)).ToArray();

                var levelResults = await Task.WhenAll(tasks);
                sw.Stop();

                runs.AddRange(levelResults);

                var succeeded = levelResults.Where(r => r.Success).ToList();
                double wallClockSeconds = sw.Elapsed.TotalSeconds;
                double aggregateTokPerSec = wallClockSeconds > 0
                    ? succeeded.Sum(r => r.CompletionTokens) / wallClockSeconds
                    : 0;

                summaries.Add(new AgentConcurrencyLevelSummary
                {
                    ConcurrencyLevel = level,
                    Repeat = repeat,
                    WallClockMs = sw.Elapsed.TotalMilliseconds,
                    SuccessCount = succeeded.Count,
                    FailCount = levelResults.Length - succeeded.Count,
                    AggregateTokensPerSecond = aggregateTokPerSec,
                    AvgTtftMs = succeeded.Count > 0 ? succeeded.Average(r => r.TtftMs) : 0
                });

                onProgress($"wall {sw.Elapsed.TotalMilliseconds:F0}ms, " +
                           $"aggregate {aggregateTokPerSec:F1} tok/s, " +
                           $"{succeeded.Count}/{levelResults.Length} ok\n");
            }
        }

        return (runs, summaries);
    }

    private async Task<AgentConcurrencyRunResult> RunSingleAgentAsync(
        ChatClientAgent agent, string prompt, int concurrencyLevel, int repeat, int agentIndex, CancellationToken ct)
    {
        using Activity? activity = AgentTelemetry.ActivitySource.StartActivity("agent-concurrency.run");
        activity?.SetTag("concurrency.level", concurrencyLevel)
            .SetTag("concurrency.repeat", repeat)
            .SetTag("concurrency.agent_index", agentIndex);

        var sw = Stopwatch.StartNew();
        double ttftMs = -1;
        var sb = new StringBuilder();
        long? realCompletionTokens = null;

        try
        {
            await foreach (var update in agent.RunStreamingAsync(prompt, cancellationToken: ct))
            {
                string? text = update.Text;
                if (!string.IsNullOrEmpty(text))
                {
                    if (ttftMs < 0) ttftMs = sw.Elapsed.TotalMilliseconds;
                    sb.Append(text);
                }

                // The server's real usage block (when it sends one) rides along
                // as a UsageContent item rather than through .Text — without
                // this, every count below falls back to the char/4 estimator,
                // which badly undercounts code (far denser in tokens per
                // character than the English-prose ratio it assumes).
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usage)
                        realCompletionTokens = usage.Details.OutputTokenCount;
                }
            }
            sw.Stop();
            if (ttftMs < 0) ttftMs = sw.Elapsed.TotalMilliseconds;

            int tokens = realCompletionTokens.HasValue
                ? (int)realCompletionTokens.Value
                : TokenEstimator.EstimateTokens(sb.ToString());
            double genSeconds = (sw.Elapsed.TotalMilliseconds - ttftMs) / 1000.0;
            double tokPerSec = genSeconds >= MinReliableGenSeconds ? tokens / genSeconds : double.NaN;

            activity?.SetTag("run.ttft_ms", ttftMs)
                .SetTag("run.duration_ms", sw.Elapsed.TotalMilliseconds)
                .SetTag("run.completion_tokens", tokens);

            return new AgentConcurrencyRunResult
            {
                Success = true,
                TtftMs = ttftMs,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                CompletionTokens = tokens,
                TokensPerSecond = tokPerSec
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return new AgentConcurrencyRunResult
            {
                Success = false,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Error = ex.Message
            };
        }
    }

    // ---------------------------------------------------------------------
    // Multi-agent workflow: a role-specialized pipeline (sequential), itself
    // optionally run as several concurrent pipeline instances.
    // ---------------------------------------------------------------------

    /// <param name="onStage">Optional hook reporting the sub-stage currently in flight
    /// (e.g. "Agent benchmark: workflow x2") so callers can surface it in a UI.</param>
    public async Task<(List<AgentWorkflowStageResult> Stages, List<AgentWorkflowPipelineSummary> Pipelines)>
        RunWorkflowAsync(AgentWorkflowConfig cfg, Action<string> onProgress,
            Action<string>? onStage = null, CancellationToken ct = default)
    {
        var stageResults = new List<AgentWorkflowStageResult>();
        var pipelineSummaries = new List<AgentWorkflowPipelineSummary>();

        if (cfg.Stages.Count == 0)
        {
            onProgress("  No workflow stages configured — skipping.\n");
            return (stageResults, pipelineSummaries);
        }

        foreach (int parallelCount in cfg.ParallelPipelineCounts)
        {
            onStage?.Invoke($"Agent benchmark: workflow x{parallelCount}");
            for (int repeat = 1; repeat <= cfg.RepeatsPerLevel; repeat++)
            {
                onProgress($"  Workflow x{parallelCount} parallel pipeline(s), repeat {repeat}/{cfg.RepeatsPerLevel}... ");

                var sw = Stopwatch.StartNew();
                var tasks = Enumerable.Range(0, parallelCount)
                    .Select(i => RunSinglePipelineAsync(cfg, parallelCount, repeat, i, ct))
                    .ToArray();

                var results = await Task.WhenAll(tasks);
                sw.Stop();

                foreach (var (stages, pipeline) in results)
                {
                    stageResults.AddRange(stages);
                    pipelineSummaries.Add(pipeline);
                }

                int okCount = results.Count(r => r.Pipeline.Success);
                onProgress($"wall {sw.Elapsed.TotalMilliseconds:F0}ms, {okCount}/{results.Length} ok\n");
            }
        }

        return (stageResults, pipelineSummaries);
    }

    private async Task<(List<AgentWorkflowStageResult> Stages, AgentWorkflowPipelineSummary Pipeline)> RunSinglePipelineAsync(
        AgentWorkflowConfig cfg, int parallelCount, int repeat, int pipelineIndex, CancellationToken ct)
    {
        // Wall-clock anchor for reconstructing per-stage spans below: pipelineSw
        // gives us relative offsets (ms since pipeline start), and this ties them
        // back to absolute timestamps a trace viewer can place on a timeline.
        var pipelineStartUtc = DateTimeOffset.UtcNow;
        var pipelineSw = Stopwatch.StartNew();

        // See the comment on the equivalent agent-concurrency.batch span: forces
        // this onto its own sampled root trace instead of silently inheriting
        // (and being suppressed by) the ASP.NET Core request's unsampled activity.
        Activity.Current = null;
        using Activity? pipelineActivity = AgentTelemetry.ActivitySource.StartActivity("agent-workflow.pipeline");
        pipelineActivity?.SetTag("workflow.parallel_count", parallelCount)
            .SetTag("workflow.repeat", repeat)
            .SetTag("workflow.pipeline_index", pipelineIndex);

        // Fresh agent instances per pipeline run so concurrent runs never share workflow/agent state.
        var stageAgents = cfg.Stages.Select(s => MakeAgent(s.Instructions, cfg.MaxTokensPerStage, s.Name)).ToList();
        var workflow = AgentWorkflowBuilder.BuildSequential(stageAgents);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage> { new(ChatRole.User, cfg.Prompt) };
        var stages = new List<AgentWorkflowStageResult>();

        try
        {
            await using StreamingRun run = await InProcessExecution.RunStreamingAsync(workflow, messages, cancellationToken: ct);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            // Track per-executor (per-stage) text + first/last event timestamps as
            // events stream in, since the workflow interleaves stage output rather
            // than handing back discrete per-stage results.
            var order = new List<string>();
            var textByExecutor = new Dictionary<string, StringBuilder>();
            var firstSeenMs = new Dictionary<string, double>();
            var realTokensByExecutor = new Dictionary<string, long>();

            await foreach (WorkflowEvent evt in run.WatchStreamAsync(ct))
            {
                if (evt is AgentResponseUpdateEvent e)
                {
                    string id = e.ExecutorId;

                    // The real usage block (when the server sends one) rides
                    // along as a UsageContent item, often on a final update whose
                    // .Text is empty — check it before the text-emptiness bailout
                    // below so it isn't skipped for exactly the updates carrying it.
                    foreach (var content in e.Update.Contents)
                    {
                        if (content is UsageContent usage && usage.Details.OutputTokenCount is long outputTokens)
                            realTokensByExecutor[id] = outputTokens;
                    }

                    string? text = e.Update.Text;
                    if (string.IsNullOrEmpty(text)) continue;

                    if (!textByExecutor.TryGetValue(id, out var sb))
                    {
                        sb = new StringBuilder();
                        textByExecutor[id] = sb;
                        firstSeenMs[id] = pipelineSw.Elapsed.TotalMilliseconds;
                        order.Add(id);
                    }
                    sb.Append(text);
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
                else if (evt is ExecutorFailedEvent failedEvt)
                {
                    throw new InvalidOperationException(
                        $"Stage executor '{failedEvt.ExecutorId}' failed: {failedEvt.Data}");
                }
                else if (evt is WorkflowErrorEvent errorEvt)
                {
                    throw new InvalidOperationException("Workflow run failed.", errorEvt.Exception);
                }
            }
            pipelineSw.Stop();

            // Stage start = its first token; stage end = the next stage's first
            // token (or pipeline end for the last stage) — that "end" boundary
            // includes queueing/handoff time, same as a real pipeline would pay.
            // order[i] corresponds to cfg.Stages[i]: BuildSequential runs stages
            // strictly in order and each one emits exactly once per pipeline run.
            for (int i = 0; i < order.Count; i++)
            {
                string id = order[i];
                double startMs = firstSeenMs[id];
                double endMs = i + 1 < order.Count ? firstSeenMs[order[i + 1]] : pipelineSw.Elapsed.TotalMilliseconds;
                double durationMs = Math.Max(endMs - startMs, 0);

                int tokens = realTokensByExecutor.TryGetValue(id, out long realTokens)
                    ? (int)realTokens
                    : TokenEstimator.EstimateTokens(textByExecutor[id].ToString());
                double genSeconds = durationMs / 1000.0;
                double tokPerSec = genSeconds >= MinReliableGenSeconds ? tokens / genSeconds : double.NaN;

                // Reconstructed after the fact from the timestamps above, rather
                // than created live as each stage runs, since stage boundaries are
                // only knowable once the interleaved event stream has been walked.
                // Explicit start/end times still place it correctly on a trace
                // timeline, nested under the pipeline span via parentContext.
                using (Activity? stageActivity = AgentTelemetry.ActivitySource.StartActivity(
                           $"agent-workflow.stage.{(i < cfg.Stages.Count ? cfg.Stages[i].Name : id)}",
                           ActivityKind.Internal,
                           pipelineActivity?.Context ?? default,
                           startTime: pipelineStartUtc.AddMilliseconds(startMs)))
                {
                    stageActivity?.SetTag("stage.order", i)
                        .SetTag("stage.name", i < cfg.Stages.Count ? cfg.Stages[i].Name : id)
                        .SetTag("stage.ttft_ms", startMs)
                        .SetTag("stage.duration_ms", durationMs)
                        .SetTag("stage.completion_tokens", tokens)
                        .SetEndTime(pipelineStartUtc.AddMilliseconds(endMs).UtcDateTime);
                }

                stages.Add(new AgentWorkflowStageResult
                {
                    PipelineParallelCount = parallelCount,
                    Repeat = repeat,
                    PipelineIndex = pipelineIndex,
                    StageOrder = i,
                    StageName = i < cfg.Stages.Count ? cfg.Stages[i].Name : id,
                    Success = true,
                    TtftMs = startMs,
                    DurationMs = durationMs,
                    CompletionTokens = tokens,
                    TokensPerSecond = tokPerSec
                });
            }

            pipelineActivity?.SetTag("workflow.total_duration_ms", pipelineSw.Elapsed.TotalMilliseconds);

            return (stages, new AgentWorkflowPipelineSummary
            {
                PipelineParallelCount = parallelCount,
                Repeat = repeat,
                PipelineIndex = pipelineIndex,
                TotalDurationMs = pipelineSw.Elapsed.TotalMilliseconds,
                Success = true
            });
        }
        catch (Exception ex)
        {
            pipelineSw.Stop();
            pipelineActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            return (stages, new AgentWorkflowPipelineSummary
            {
                PipelineParallelCount = parallelCount,
                Repeat = repeat,
                PipelineIndex = pipelineIndex,
                TotalDurationMs = pipelineSw.Elapsed.TotalMilliseconds,
                Success = false,
                Error = ex.Message
            });
        }
    }
}
