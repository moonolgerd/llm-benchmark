using System.Text.Json.Serialization;

namespace LlmBenchmark.Models;

/// <summary>
/// Config for the Microsoft Agent Framework benchmark mode. This runs alongside
/// the existing raw-HTTP OpenAiClient benchmark (see Program.cs) and exercises
/// the same local server through Microsoft.Agents.AI instead, in two shapes:
/// many identical agents hammering the server concurrently (concurrencyLoad),
/// and a multi-stage agent pipeline built with AgentWorkflowBuilder (workflow).
/// </summary>
public class AgentBenchmarkConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = "";

    [JsonPropertyName("concurrencyLoad")]
    public ConcurrencyLoadConfig ConcurrencyLoad { get; set; } = new();

    [JsonPropertyName("workflow")]
    public AgentWorkflowConfig Workflow { get; set; } = new();
}

/// <summary>
/// N identical agents fire the same prompt at the server at the same time, at
/// each concurrency level in AgentCounts, so TTFT/tok-per-sec degradation under
/// concurrent load can be compared against the sequential baseline.
/// </summary>
public class ConcurrencyLoadConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("agentCounts")]
    public List<int> AgentCounts { get; set; } = new() { 1, 2, 4 };

    [JsonPropertyName("repeatsPerLevel")]
    public int RepeatsPerLevel { get; set; } = 1;

    [JsonPropertyName("instructions")]
    public string? Instructions { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 400;
}

/// <summary>
/// A fixed pipeline of role-specialized agents run in sequence via
/// AgentWorkflowBuilder.BuildSequential, each consuming the previous agent's
/// output. ParallelPipelineCounts additionally runs several independent copies
/// of that whole pipeline at once, to see how a realistic multi-agent workflow
/// (not just single-turn chat) holds up under concurrent load.
/// </summary>
public class AgentWorkflowConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("stages")]
    public List<WorkflowStageSpec> Stages { get; set; } = new();

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("maxTokensPerStage")]
    public int MaxTokensPerStage { get; set; } = 500;

    [JsonPropertyName("parallelPipelineCounts")]
    public List<int> ParallelPipelineCounts { get; set; } = new() { 1 };

    [JsonPropertyName("repeatsPerLevel")]
    public int RepeatsPerLevel { get; set; } = 1;
}

public class WorkflowStageSpec
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("instructions")]
    public string Instructions { get; set; } = "";
}
