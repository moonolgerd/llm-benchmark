namespace LlmBenchmark.Models;

/// <summary>
/// One task+attempt run through one scaffold ("RawHttp" or "AgentFramework"),
/// same model and same prompt as the other scaffold's matching row — the two
/// rows for a given (TaskName, Attempt) are directly comparable.
/// </summary>
public class ScaffoldComparisonResult
{
    public string ModelId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int Attempt { get; set; }
    public string Scaffold { get; set; } = "";
    public bool Success { get; set; }
    public double TtftMs { get; set; }
    public double TotalDurationMs { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public double TokensPerSecond { get; set; }
    public string? Error { get; set; }
}
