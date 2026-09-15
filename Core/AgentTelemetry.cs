using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace LlmBenchmark;

/// <summary>
/// Central OpenTelemetry wiring shared by the CLI, DevUI host, and Dashboard so
/// a benchmark run's agent/workflow activity shows up as traces (in the .NET
/// Aspire Dashboard, or any other OTLP backend) instead of only in CSV output.
///
/// Exporting only turns on when OTEL_EXPORTER_OTLP_ENDPOINT is set. Aspire
/// injects that (plus protocol/headers) automatically into any project
/// resource its AppHost orchestrates — currently just the Dashboard project —
/// so `dotnet run --project AppHost` gets traces for free. The plain CLI and
/// `--devui` aren't Aspire-orchestrated; point them at a collector manually
/// (e.g. a standalone Aspire Dashboard container) by setting the same env var
/// before running, or they stay a no-op.
/// </summary>
public static class AgentTelemetry
{
    /// <summary>Source for the benchmark's own pipeline/stage/run spans.</summary>
    public const string SourceName = "LlmBenchmark.AgentFramework";

    /// <summary>
    /// Source name passed to Microsoft.Extensions.AI's chat-client
    /// UseOpenTelemetry(sourceName:), so the gen_ai.* spans it emits per LLM
    /// call land under our own namespace instead of its experimental default.
    /// </summary>
    public const string ChatClientSourceName = SourceName + ".ChatClient";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    /// <summary>
    /// For non-hosted entry points (the CLI's top-level Program.cs): builds and
    /// starts a TracerProvider the caller owns via `using`. Returns null when
    /// telemetry isn't enabled; `using var x = TryCreateTracerProvider(...)`
    /// is safe even when x is null (Dispose is simply skipped).
    /// </summary>
    public static TracerProvider? TryCreateTracerProvider(string serviceName)
    {
        if (!IsEnabled) return null;

        return Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
            .AddSource(SourceName)
            .AddSource(ChatClientSourceName)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter()
            .Build();
    }

    /// <summary>
    /// For hosted entry points (Dashboard, DevUI): registers tracing + metrics
    /// with DI so the host owns the provider's lifetime. No-op when telemetry
    /// isn't enabled, so a plain standalone run never dials a collector.
    /// </summary>
    public static void AddToServices(IServiceCollection services, string serviceName)
    {
        if (!IsEnabled) return;

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddSource(SourceName)
                .AddSource(ChatClientSourceName)
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddHttpClientInstrumentation()
                .AddOtlpExporter());
    }
}
