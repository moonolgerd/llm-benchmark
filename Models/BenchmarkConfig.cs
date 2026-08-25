using System.Text.Json.Serialization;

namespace LlmBenchmark.Models;

public class BenchmarkConfig
{
    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = "http://localhost:1234/v1";

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = "not-needed";

    [JsonPropertyName("models")]
    public List<ModelEntry> Models { get; set; } = new();

    [JsonPropertyName("tasks")]
    public List<TaskPrompt> Tasks { get; set; } = new();

    [JsonPropertyName("contextProbe")]
    public ContextProbeConfig ContextProbe { get; set; } = new();

    [JsonPropertyName("repeatsPerTask")]
    public int RepeatsPerTask { get; set; } = 2;

    [JsonPropertyName("sampling")]
    public SamplingConfig Sampling { get; set; } = new();

    [JsonPropertyName("agentBenchmark")]
    public AgentBenchmarkConfig AgentBenchmark { get; set; } = new();
}

public class SamplingConfig
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.7;

    [JsonPropertyName("topP")]
    public double TopP { get; set; } = 0.8;

    [JsonPropertyName("topK")]
    public int TopK { get; set; } = 20;

    [JsonPropertyName("minP")]
    public double MinP { get; set; } = 0.05;

    [JsonPropertyName("repetitionPenalty")]
    public double RepetitionPenalty { get; set; } = 1.1;

    // Off by default for the benchmark: these are reasoning-capable models, and
    // with it on (or left to the server's own default) they can spend the whole
    // max_tokens budget on chain-of-thought before ever emitting a final answer,
    // which is what caused the empty-transcript / bogus-tok/s bug we hit earlier.
    [JsonPropertyName("enableThinking")]
    public bool EnableThinking { get; set; } = false;

    // Some servers (e.g. FreeToken Desktop) ignore enableThinking entirely and
    // always reason, defaulting to their most verbose effort tier — which can
    // burn the whole max_tokens budget on chain-of-thought with nothing left
    // for the actual answer. Null omits the field so servers that don't
    // support it (and don't ignore unknown fields) aren't affected.
    [JsonPropertyName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }
}

public class ModelEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
}

public class TaskPrompt
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("systemPrompt")]
    public string? SystemPrompt { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 500;
}

public class ContextProbeConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("tokenSteps")]
    public List<int> TokenSteps { get; set; } = new() { 8192, 16384, 32768, 65536 };

    [JsonPropertyName("fillerText")]
    public string FillerText { get; set; } =
        "The quick brown fox jumps over the lazy dog near the riverbank at dusk. ";
}
