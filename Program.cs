using LlmBenchmark;
using LlmBenchmark.Models;

// No-op unless OTEL_EXPORTER_OTLP_ENDPOINT is set (see AgentTelemetry) — point it
// at a collector (e.g. a standalone Aspire Dashboard container) to see agent/
// workflow traces from a plain CLI run; disposed at process exit to flush spans.
using var tracerProvider = AgentTelemetry.TryCreateTracerProvider("LlmBenchmark.Cli");

bool devUiMode = args.Contains("--devui");
string configPath = args.FirstOrDefault(a => a != "--devui") ?? "config.json";
if (!File.Exists(configPath))
{
    Console.WriteLine($"Config file not found: {configPath}");
    Console.WriteLine("Pass a path as the first argument, or place config.json next to the exe.");
    return 1;
}

// BenchmarkRunner logs with bare "\n" terminators (the dashboard appends that
// text to a buffer verbatim); map them onto the platform line ending here so
// console output stays byte-identical to the pre-refactor CLI.
Action<string> consoleLog = s => Console.Write(s.Replace("\n", Environment.NewLine));

BenchmarkConfig config;
try
{
    config = ConfigLoader.Load(configPath, consoleLog);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to load config '{configPath}': {ex.Message}");
    return 1;
}

if (devUiMode)
{
    await DevUiHost.RunAsync(config);
    return 0;
}

var runner = new BenchmarkRunner(config);
BenchmarkRunResult results = await runner.RunAsync(consoleLog);

string outDir = "results";
string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
ResultsWriter.WriteRun(outDir, timestamp, results);

Console.WriteLine();
Console.WriteLine($"Done. Results written to {Path.GetFullPath(outDir)}");
Console.WriteLine("  - speed-*.csv                    : TTFT / tok-s / VRAM per task+attempt");
Console.WriteLine("  - context-probe-*.csv            : max usable context before failure/offload");
Console.WriteLine("  - quality-transcripts-*.txt      : raw outputs for you to grade by hand");
if (config.AgentBenchmark.Enabled)
{
    Console.WriteLine("  - agent-concurrency-*.csv        : per-agent TTFT/tok-s under N-way concurrent load (Agent Framework)");
    Console.WriteLine("  - agent-concurrency-summary-*.csv: aggregate throughput per concurrency level");
    Console.WriteLine("  - agent-workflow-stages-*.csv    : per-stage timing within the multi-agent pipeline");
    Console.WriteLine("  - agent-workflow-pipelines-*.csv : whole-pipeline duration per concurrent run");
}

return 0;
