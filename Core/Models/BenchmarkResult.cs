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
    /// <summary>Prompt-processing rate: prompt tokens / TTFT. NaN when TTFT is unreliable (no first-token signal).</summary>
    public double PrefillTokensPerSecond { get; set; } = double.NaN;
    public int VramUsedMbBefore { get; set; }
    public int VramUsedMbAfter { get; set; }
    /// <summary>Mean board power (W) sampled across the request; -1 when nvidia-smi gave no reading.</summary>
    public double AvgPowerW { get; set; } = -1;
    /// <summary>Peak board power (W) sampled across the request; -1 when unknown.</summary>
    public double PeakPowerW { get; set; } = -1;
    /// <summary>Decode efficiency: completion tokens / (AvgPowerW * generation seconds). NaN when power or tok/s is unavailable.</summary>
    public double TokensPerJoule { get; set; } = double.NaN;
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
