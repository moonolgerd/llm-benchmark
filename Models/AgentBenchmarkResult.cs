namespace LlmBenchmark.Models;

/// <summary>One agent's run within a concurrency-load batch (see AgentConcurrencyLevelSummary for the batch-level view).</summary>
public class AgentConcurrencyRunResult
{
    public int ConcurrencyLevel { get; set; }
    public int Repeat { get; set; }
    public int AgentIndex { get; set; }
    public bool Success { get; set; }
    public double TtftMs { get; set; }
    public double DurationMs { get; set; }
    public int CompletionTokens { get; set; }
    public double TokensPerSecond { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Aggregate view of one concurrency level + repeat: how much combined throughput
/// the server sustained while all agents in the batch were in flight together,
/// as opposed to each agent's own individual tok/s.
/// </summary>
public class AgentConcurrencyLevelSummary
{
    public int ConcurrencyLevel { get; set; }
    public int Repeat { get; set; }
    public double WallClockMs { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public double AggregateTokensPerSecond { get; set; }
    public double AvgTtftMs { get; set; }
}

/// <summary>One pipeline stage's contribution within one workflow run.</summary>
public class AgentWorkflowStageResult
{
    public int PipelineParallelCount { get; set; }
    public int Repeat { get; set; }
    public int PipelineIndex { get; set; }
    public int StageOrder { get; set; }
    public string StageName { get; set; } = "";
    public bool Success { get; set; }
    public double TtftMs { get; set; }
    public double DurationMs { get; set; }
    public int CompletionTokens { get; set; }
    public double TokensPerSecond { get; set; }
    public string? Error { get; set; }
}

/// <summary>Whole-pipeline (all stages) timing for one workflow run, one row per concurrent pipeline instance.</summary>
public class AgentWorkflowPipelineSummary
{
    public int PipelineParallelCount { get; set; }
    public int Repeat { get; set; }
    public int PipelineIndex { get; set; }
    public double TotalDurationMs { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
