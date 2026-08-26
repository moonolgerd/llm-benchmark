namespace LlmBenchmark.Models;

/// <summary>
/// Everything one benchmark run collected, handed to ResultsWriter.WriteRun by
/// both the CLI and the dashboard so they produce identical timestamped files.
/// </summary>
public sealed class BenchmarkRunResult
{
    public List<SpeedResult> SpeedResults { get; } = new();
    public List<ContextProbeResult> ContextResults { get; } = new();
    public List<QualityRecord> QualityRecords { get; } = new();
    public List<AgentConcurrencyRunResult> AgentConcurrencyRuns { get; } = new();
    public List<AgentConcurrencyLevelSummary> AgentConcurrencySummaries { get; } = new();
    public List<AgentWorkflowStageResult> WorkflowStages { get; } = new();
    public List<AgentWorkflowPipelineSummary> WorkflowPipelines { get; } = new();
}
