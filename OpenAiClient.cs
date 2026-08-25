using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LlmBenchmark.Models;

namespace LlmBenchmark;

public class StreamedChatResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public double TtftMs { get; set; }
    public double TotalDurationMs { get; set; }
    public string FullText { get; set; } = "";
    public bool AnswerIsReasoningOnly { get; set; } // true if only reasoning_content arrived, no final content
    public int? PromptTokens { get; set; }      // populated only if server sends usage
    public int? CompletionTokens { get; set; }  // populated only if server sends usage
}

/// <summary>
/// Minimal client for the OpenAI-compatible /v1/chat/completions endpoint that
/// Unsloth Desktop exposes locally. Streams the response so we can capture
/// time-to-first-token, not just total latency.
/// </summary>
public class OpenAiClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OpenAiClient(string baseUrl, string apiKey)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>
    /// Reads the device-fitted context window Unsloth auto-sized for this model
    /// (exposed on /v1/models once the model is loaded). This reflects real
    /// available VRAM at load time, not the GGUF's native context — it's the
    /// actual ceiling requests will be silently truncated against, so it's more
    /// trustworthy than inferring a ceiling from probe timings/failures.
    /// </summary>
    public async Task<int?> GetModelContextLimitAsync(string modelId, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{_baseUrl}/models", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return null;

            // Requests can use "repo:quant" (e.g. to trigger an on-demand download of a
            // specific quant), but /v1/models reports the loaded entry back under the
            // bare repo id only — match on that so the lookup doesn't silently miss and
            // let the probe run unclamped past the real cap.
            string bareModelId = modelId.Split(':')[0];

            foreach (var entry in data.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var idEl) || idEl.GetString() != bareModelId)
                    continue;

                if (entry.TryGetProperty("max_context_length", out var maxCtxEl) &&
                    maxCtxEl.ValueKind == JsonValueKind.Number)
                    return maxCtxEl.GetInt32();

                if (entry.TryGetProperty("context_length", out var ctxEl) &&
                    ctxEl.ValueKind == JsonValueKind.Number)
                    return ctxEl.GetInt32();

                return null;
            }

            return null;
        }
        catch
        {
            // Best-effort — if the server doesn't expose this, the probe just
            // runs unclamped as before.
            return null;
        }
    }

    /// <summary>
    /// Sends a trivial request to trigger Unsloth's auto-load-on-request and
    /// blocks (with retries) until the model responds successfully or the
    /// timeout elapses. Load time itself is intentionally NOT counted toward
    /// any benchmark metric — this just gets the model hot before timing starts.
    /// </summary>
    public async Task<(bool ready, double loadSeconds, string? lastError)> WarmUpModelAsync(
        string modelId,
        TimeSpan timeout,
        SamplingConfig sampling,
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        string? lastError = null;
        TimeSpan pollInterval = TimeSpan.FromSeconds(3);

        while (sw.Elapsed < timeout)
        {
            var result = await StreamChatCompletionAsync(
                modelId, null, "Reply with just: ok", maxTokens: 5, sampling, ct);

            if (result.Success)
            {
                return (true, sw.Elapsed.TotalSeconds, null);
            }

            lastError = result.Error;
            onProgress?.Invoke($"not ready yet ({sw.Elapsed.TotalSeconds:F0}s): {result.Error}");
            await Task.Delay(pollInterval, ct);
        }

        return (false, sw.Elapsed.TotalSeconds, lastError);
    }

    public async Task<StreamedChatResult> StreamChatCompletionAsync(
        string modelId,
        string? systemPrompt,
        string userPrompt,
        int maxTokens,
        SamplingConfig sampling,
        CancellationToken ct = default)
    {
        var result = new StreamedChatResult();
        var sw = Stopwatch.StartNew();
        bool firstTokenSeen = false;
        var sb = new StringBuilder();
        var sbReasoning = new StringBuilder();

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userPrompt });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = modelId,
            ["messages"] = messages,
            ["max_tokens"] = maxTokens,
            ["temperature"] = sampling.Temperature,
            ["top_p"] = sampling.TopP,
            ["top_k"] = sampling.TopK,
            ["min_p"] = sampling.MinP,
            ["repetition_penalty"] = sampling.RepetitionPenalty,
            ["enable_thinking"] = sampling.EnableThinking,
            ["stream"] = true,
            // Some OpenAI-compatible servers only emit a usage block on the
            // final chunk when this is set. Harmless if the server ignores it.
            ["stream_options"] = new { include_usage = true }
        };
        if (!string.IsNullOrWhiteSpace(sampling.ReasoningEffort))
            payload["reasoning_effort"] = sampling.ReasoningEffort;

        var json = JsonSerializer.Serialize(payload);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                result.Success = false;
                result.Error = $"HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}";
                return result;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:")) continue;

                string data = line["data:".Length..].Trim();
                if (data == "[DONE]") break;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; } // skip malformed keep-alive lines

                using (doc)
                {
                    var root = doc.RootElement;

                    // Some OpenAI-compatible servers (e.g. FreeToken Desktop) return
                    // HTTP 200 for a streaming request and only surface a rejection
                    // (like "prompt too long") as an in-band SSE event carrying an
                    // "error" object, rather than a non-2xx status or a malformed
                    // stream. Without this check, IsSuccessStatusCode above already
                    // passed, no content/reasoning chunk ever arrives, and the
                    // request was silently recorded as a "successful" 0-token
                    // response instead of the failure it actually is.
                    if (root.TryGetProperty("error", out var errorEl))
                    {
                        result.Success = false;
                        result.Error = errorEl.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                            ? msgEl.GetString()
                            : errorEl.ToString();
                        result.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
                        return result;
                    }

                    if (root.TryGetProperty("usage", out var usageEl) &&
                        usageEl.ValueKind == JsonValueKind.Object)
                    {
                        if (usageEl.TryGetProperty("prompt_tokens", out var pt))
                            result.PromptTokens = pt.GetInt32();
                        if (usageEl.TryGetProperty("completion_tokens", out var ct2))
                            result.CompletionTokens = ct2.GetInt32();
                    }

                    if (root.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array &&
                        choices.GetArrayLength() > 0)
                    {
                        var choice = choices[0];
                        if (choice.TryGetProperty("delta", out var delta))
                        {
                            // Final answer text.
                            if (delta.TryGetProperty("content", out var contentEl) &&
                                contentEl.ValueKind == JsonValueKind.String)
                            {
                                string chunk = contentEl.GetString() ?? "";
                                if (chunk.Length > 0)
                                {
                                    if (!firstTokenSeen)
                                    {
                                        firstTokenSeen = true;
                                        result.TtftMs = sw.Elapsed.TotalMilliseconds;
                                    }
                                    sb.Append(chunk);
                                }
                            }

                            // Some reasoning models (DeepSeek-R1 style / vLLM / llama.cpp
                            // reasoning mode) stream chain-of-thought under a separate
                            // field instead of "content". We still need this to know when
                            // the model actually started producing tokens, or TTFT/duration
                            // collapse to the same value and downstream tok/s math blows up.
                            string? reasoningChunk =
                                (delta.TryGetProperty("reasoning_content", out var rcEl) && rcEl.ValueKind == JsonValueKind.String)
                                    ? rcEl.GetString()
                                    : (delta.TryGetProperty("reasoning", out var rEl) && rEl.ValueKind == JsonValueKind.String)
                                        ? rEl.GetString()
                                        : null;

                            if (!string.IsNullOrEmpty(reasoningChunk))
                            {
                                if (!firstTokenSeen)
                                {
                                    firstTokenSeen = true;
                                    result.TtftMs = sw.Elapsed.TotalMilliseconds;
                                }
                                sbReasoning.Append(reasoningChunk);
                            }
                        }
                    }
                }
            }

            sw.Stop();
            result.Success = true;
            result.TotalDurationMs = sw.Elapsed.TotalMilliseconds;

            if (sb.Length > 0)
            {
                result.FullText = sb.ToString();
            }
            else if (sbReasoning.Length > 0)
            {
                // Model spent its whole budget on reasoning tokens with no final
                // answer — surface that plainly instead of writing an empty
                // transcript that looks like a silent failure.
                result.AnswerIsReasoningOnly = true;
                result.FullText = "[reasoning only, no final answer content]\n" + sbReasoning.ToString();
            }

            if (!firstTokenSeen)
            {
                // No content or reasoning chunks arrived at all — treat TTFT as
                // the full duration so it's still visible as an outlier in the
                // results (downstream tok/s math treats this as unreliable).
                result.TtftMs = result.TotalDurationMs;
            }
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.Error = ex.Message;
            result.TotalDurationMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }
    }
}
