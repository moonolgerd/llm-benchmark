namespace LlmBenchmark;

/// <summary>
/// Rough token estimate (~4 chars/token, English-text heuristic) used only
/// when the server doesn't report real usage numbers in the stream. Prefer
/// the server-reported usage field whenever it's present — this is a fallback,
/// not a substitute.
/// </summary>
public static class TokenEstimator
{
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Math.Max(1, text.Length / 4);
    }
}
