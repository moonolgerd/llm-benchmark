using System.Text.Json;
using LlmBenchmark.Models;

namespace LlmBenchmark;

/// <summary>
/// Loads a benchmark config JSON and resolves ${VAR_NAME} apiKey placeholders
/// from the environment (moved out of Program.cs so the CLI and the dashboard
/// share one code path).
/// </summary>
public static class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static BenchmarkConfig Load(string path, Action<string>? log = null)
    {
        string json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<BenchmarkConfig>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to parse {path}");
        config.ApiKey = ResolveEnvPlaceholder(config.ApiKey, log);
        return config;
    }

    /// <summary>
    /// Secrets stay out of the config file: an apiKey of the form ${VAR_NAME} is
    /// resolved from the environment at startup (e.g. ${UNSLOTH_API_KEY}). If the
    /// variable is unset, warn and continue unauthenticated instead of sending
    /// the literal placeholder.
    /// </summary>
    internal static string ResolveEnvPlaceholder(string value, Action<string>? log = null)
    {
        if (value.Length >= 3 && value.StartsWith("${") && value.EndsWith("}"))
        {
            string varName = value[2..^1];
            string? resolved = Environment.GetEnvironmentVariable(varName);
            if (string.IsNullOrEmpty(resolved))
            {
                log?.Invoke($"Warning: apiKey placeholder ${{{varName}}} — environment variable '{varName}' is not set; requests will go out unauthenticated.\n");
            }
            return resolved ?? "";
        }
        return value;
    }
}
