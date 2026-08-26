using System.Globalization;
using LlmBenchmark.Models;

namespace LlmBenchmark;

public static class ResultsWriter
{
    /// <summary>
    /// Writes one run's timestamped result files (the same set the CLI always
    /// produced) into outDir. Both the CLI and the dashboard call this so their
    /// runs land in results/ identically; partial (cancelled) runs are written
    /// too. Agent files are written when present — a disabled or skipped
    /// agentBenchmark leaves those lists empty.
    /// </summary>
    public static void WriteRun(string outDir, string timestamp, BenchmarkRunResult run)
    {
        Directory.CreateDirectory(outDir);
        WriteSpeedCsv(Path.Combine(outDir, $"speed-{timestamp}.csv"), run.SpeedResults);
        WriteContextProbeCsv(Path.Combine(outDir, $"context-probe-{timestamp}.csv"), run.ContextResults);
        WriteQualityTranscripts(Path.Combine(outDir, $"quality-transcripts-{timestamp}.txt"), run.QualityRecords);

        if (run.AgentConcurrencyRuns.Count > 0 || run.AgentConcurrencySummaries.Count > 0)
        {
            WriteAgentConcurrencyCsv(Path.Combine(outDir, $"agent-concurrency-{timestamp}.csv"), run.AgentConcurrencyRuns);
            WriteAgentConcurrencySummaryCsv(Path.Combine(outDir, $"agent-concurrency-summary-{timestamp}.csv"), run.AgentConcurrencySummaries);
        }

        if (run.WorkflowStages.Count > 0 || run.WorkflowPipelines.Count > 0)
        {
            WriteAgentWorkflowStagesCsv(Path.Combine(outDir, $"agent-workflow-stages-{timestamp}.csv"), run.WorkflowStages);
            WriteAgentWorkflowPipelinesCsv(Path.Combine(outDir, $"agent-workflow-pipelines-{timestamp}.csv"), run.WorkflowPipelines);
        }
    }

    public static void WriteSpeedCsv(string path, IEnumerable<SpeedResult> results)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("ModelId,TaskName,Attempt,Success,TtftMs,TotalDurationMs," +
                          "PromptTokensEstimate,CompletionTokensEstimate,TokensPerSecond," +
                          "VramUsedMbBefore,VramUsedMbAfter,Error");

        foreach (var r in results)
        {
            writer.WriteLine(string.Join(",",
                Csv(r.ModelId), Csv(r.TaskName), r.Attempt, r.Success,
                r.TtftMs.ToString("F1", CultureInfo.InvariantCulture),
                r.TotalDurationMs.ToString("F1", CultureInfo.InvariantCulture),
                r.PromptTokensEstimate, r.CompletionTokensEstimate,
                r.TokensPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                r.VramUsedMbBefore, r.VramUsedMbAfter, Csv(r.Error ?? "")));
        }
    }

    public static void WriteContextProbeCsv(string path, IEnumerable<ContextProbeResult> results)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("ModelId,RequestedContextTokens,DeviceMaxContextTokens,Succeeded,TotalDurationMs,VramUsedMbAfter,Error");

        foreach (var r in results)
        {
            writer.WriteLine(string.Join(",",
                Csv(r.ModelId), r.RequestedContextTokens,
                r.DeviceMaxContextTokens?.ToString(CultureInfo.InvariantCulture) ?? "",
                r.Succeeded,
                r.TotalDurationMs.ToString("F1", CultureInfo.InvariantCulture),
                r.VramUsedMbAfter, Csv(r.Error ?? "")));
        }
    }

    public static void WriteAgentConcurrencyCsv(string path, IEnumerable<AgentConcurrencyRunResult> results)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("ConcurrencyLevel,Repeat,AgentIndex,Success,TtftMs,DurationMs,CompletionTokens,TokensPerSecond,Error");

        foreach (var r in results)
        {
            writer.WriteLine(string.Join(",",
                r.ConcurrencyLevel, r.Repeat, r.AgentIndex, r.Success,
                r.TtftMs.ToString("F1", CultureInfo.InvariantCulture),
                r.DurationMs.ToString("F1", CultureInfo.InvariantCulture),
                r.CompletionTokens,
                r.TokensPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                Csv(r.Error ?? "")));
        }
    }

    public static void WriteAgentConcurrencySummaryCsv(string path, IEnumerable<AgentConcurrencyLevelSummary> summaries)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("ConcurrencyLevel,Repeat,WallClockMs,SuccessCount,FailCount,AggregateTokensPerSecond,AvgTtftMs");

        foreach (var s in summaries)
        {
            writer.WriteLine(string.Join(",",
                s.ConcurrencyLevel, s.Repeat,
                s.WallClockMs.ToString("F1", CultureInfo.InvariantCulture),
                s.SuccessCount, s.FailCount,
                s.AggregateTokensPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                s.AvgTtftMs.ToString("F1", CultureInfo.InvariantCulture)));
        }
    }

    public static void WriteAgentWorkflowStagesCsv(string path, IEnumerable<AgentWorkflowStageResult> results)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("PipelineParallelCount,Repeat,PipelineIndex,StageOrder,StageName,Success,TtftMs,DurationMs,CompletionTokens,TokensPerSecond,Error");

        foreach (var r in results)
        {
            writer.WriteLine(string.Join(",",
                r.PipelineParallelCount, r.Repeat, r.PipelineIndex, r.StageOrder, Csv(r.StageName), r.Success,
                r.TtftMs.ToString("F1", CultureInfo.InvariantCulture),
                r.DurationMs.ToString("F1", CultureInfo.InvariantCulture),
                r.CompletionTokens,
                r.TokensPerSecond.ToString("F2", CultureInfo.InvariantCulture),
                Csv(r.Error ?? "")));
        }
    }

    public static void WriteAgentWorkflowPipelinesCsv(string path, IEnumerable<AgentWorkflowPipelineSummary> summaries)
    {
        using var writer = new StreamWriter(path, append: false);
        writer.WriteLine("PipelineParallelCount,Repeat,PipelineIndex,TotalDurationMs,Success,Error");

        foreach (var s in summaries)
        {
            writer.WriteLine(string.Join(",",
                s.PipelineParallelCount, s.Repeat, s.PipelineIndex,
                s.TotalDurationMs.ToString("F1", CultureInfo.InvariantCulture),
                s.Success, Csv(s.Error ?? "")));
        }
    }

    public static void WriteQualityTranscripts(string path, IEnumerable<QualityRecord> records)
    {
        using var writer = new StreamWriter(path, append: false);
        foreach (var r in records)
        {
            writer.WriteLine($"=== {r.ModelId} :: {r.TaskName} :: attempt {r.Attempt} ===");
            writer.WriteLine(r.ResponseText);
            writer.WriteLine();
            writer.WriteLine(new string('-', 80));
            writer.WriteLine();
        }
    }

    private static string Csv(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }
        return field;
    }
}
