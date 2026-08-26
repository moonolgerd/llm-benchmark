// Aspire AppHost: orchestrates the dashboard. The LLM server itself (Unsloth /
// FreeToken Desktop) is NOT a managed resource — it's assumed already running,
// and the dashboard just points at whatever baseUrl its config says.
//
// Env inheritance: child processes inherit the AppHost's environment, so
// UNSLOTH_API_KEY set on the machine reaches the dashboard and resolves
// ${UNSLOTH_API_KEY} in config.json as before — no explicit env plumbing needed.

var builder = DistributedApplication.CreateBuilder(args);

// Pin the same fixed port the dashboard uses standalone, so the URL is
// identical in both modes and the Aspire Dashboard can link to it. (Recent
// Aspire versions don't implicitly expose project resources — without this
// there'd be no endpoint at all.)
builder.AddProject<Projects.LlmBenchmark_Dashboard>("dashboard")
       .WithHttpEndpoint(port: 5274);

builder.Build().Run();
