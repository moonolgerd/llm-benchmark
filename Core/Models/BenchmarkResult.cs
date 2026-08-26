namespace LlmBenchmark.Models;

public class SpeedResult
{
    public string ModelId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int Attempt { get; set; }
    public double TtftMs { get; set; }
    public double TotalDurationMs { get; set; }
    public int PromptTokensEstimate { get; set; }
    public int CompletionTokensEstimate { get; set; }
    public double TokensPerSecond { get; set; }
    public int VramUsedMbBefore { get; set; }
    public int VramUsedMbAfter { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class ContextProbeResult
{
    public string ModelId { get; set; } = "";
    public int RequestedContextTokens { get; set; }
    public int? DeviceMaxContextTokens { get; set; } // from /v1/models — Unsloth's auto-fit ceiling for this model on this GPU
    public bool Succeeded { get; set; }
    public double TotalDurationMs { get; set; }
    public int VramUsedMbAfter { get; set; }
    public string? Error { get; set; }
}

public class QualityRecord
{
    public string ModelId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int Attempt { get; set; }
    public string ResponseText { get; set; } = "";
    // Grading is manual/offline — this just captures the raw output for review.
}
