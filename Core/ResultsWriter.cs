using System.Globalization;
using LlmBenchmark.Models;

namespace LlmBenchmark;

public static class ResultsWriter
{
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
