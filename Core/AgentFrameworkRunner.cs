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

    // Shared with DevUiHost so the interactive DevUI session talks to the same
    // local server the same way the benchmark does.
    internal static IChatClient CreateChatClient(string baseUrl, string apiKey, string modelId)
    {
        var credential = new ApiKeyCredential(string.IsNullOrWhiteSpace(apiKey) ? "not-needed" : apiKey);
        var options = new OpenAIClientOptions { Endpoint = new Uri(baseUrl) };
        var openAiClient = new OpenAIClient(credential, options);
        return openAiClient.GetChatClient(modelId).AsIChatClient();
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
    internal static ChatOptions BuildChatOptions(string? instructions, int maxTokens) => new()
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

    public async Task<(List<AgentConcurrencyRunResult> Runs, List<AgentConcurrencyLevelSummary> Summaries)>
        RunConcurrencyLoadAsync(ConcurrencyLoadConfig cfg, Action<string> onProgress, CancellationToken ct = default)
    {
        var runs = new List<AgentConcurrencyRunResult>();
        var summaries = new List<AgentConcurrencyLevelSummary>();

        foreach (int level in cfg.AgentCounts)
        {
            for (int repeat = 1; repeat <= cfg.RepeatsPerLevel; repeat++)
            {
                onProgress($"  Concurrency x{level}, repeat {repeat}/{cfg.RepeatsPerLevel}... ");

                var agents = Enumerable.Range(0, level)
                    .Select(_ => MakeAgent(cfg.Instructions, cfg.MaxTokens))
                    .ToList();

                var sw = Stopwatch.StartNew();
                var tasks = agents.Select((agent, i) => RunSingleAgentAsync(agent, cfg.Prompt, ct)
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
        ChatClientAgent agent, string prompt, CancellationToken ct)
    {
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

    public async Task<(List<AgentWorkflowStageResult> Stages, List<AgentWorkflowPipelineSummary> Pipelines)>
        RunWorkflowAsync(AgentWorkflowConfig cfg, Action<string> onProgress, CancellationToken ct = default)
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
        var pipelineSw = Stopwatch.StartNew();

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
