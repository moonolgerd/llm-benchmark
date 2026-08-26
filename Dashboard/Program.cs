using System.Text.Json;
using LlmBenchmark;
using LlmBenchmark.Dashboard;
using LlmBenchmark.Models;

var builder = WebApplication.CreateBuilder(args);

// Fixed port for standalone runs, overridable via DASHBOARD_URL. Under Aspire,
// ASPNETCORE_URLS is set for the resource and we leave it alone so the assigned
// URL wins (the Aspire Dashboard links to it).
string? dashboardUrl = Environment.GetEnvironmentVariable("DASHBOARD_URL");
if (dashboardUrl is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    dashboardUrl = "http://localhost:5274";
if (dashboardUrl is not null)
    builder.WebHost.UseUrls(dashboardUrl);

builder.Services.AddSingleton<RunController>();
builder.Services.AddSingleton<ResultsService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------ run lifecycle

app.MapGet("/api/status", (RunController run) => Results.Json(run.Status()));

app.MapPost("/api/run", (RunController run, RunRequest? request) =>
{
    string configPath = string.IsNullOrWhiteSpace(request?.ConfigPath) ? "config.json" : request!.ConfigPath!;

    // Relative paths resolve against the dashboard's base directory (configs are
    // copied there at build time); absolute paths are used as-is.
    string fullPath = Path.IsPathRooted(configPath) ? configPath : Path.Combine(ResultsService.BaseDir, configPath);

    if (!File.Exists(fullPath))
        return Results.Json(new { error = $"Config file not found: '{configPath}'." }, statusCode: 400);

    // Capture config-load warnings (e.g. unset ${ENV} apiKey) so they show up in
    // the run's log buffer instead of vanishing.
    var loadWarnings = new System.Text.StringBuilder();
    BenchmarkConfig config;
    try
    {
        config = ConfigLoader.Load(fullPath, s => loadWarnings.Append(s));
    }
    catch (Exception ex) when (ex is JsonException or InvalidOperationException)
    {
        return Results.Json(new { error = $"Could not parse '{configPath}': {ex.Message}" }, statusCode: 422);
    }

    // UI-provided overrides (base URL, sampling, …) are applied to the loaded
    // config here so the runner runs exactly what the user configured in the UI,
    // not just what's in the file. Applied inside TryStart so a rejected run
    // (already in flight) never mutates a config that then runs later.
    if (!run.TryStart(config, configPath, loadWarnings.ToString(), request?.Overrides, out string? startError))
        return Results.Json(new { error = startError }, statusCode: 409);

    return Results.Json(new { started = true, configPath }, statusCode: 202);
});

app.MapPost("/api/stop", (RunController run) =>
    run.TryStop()
        ? Results.Json(new { stopping = true })
        : Results.Json(new { error = "No benchmark run in progress." }, statusCode: 409));

// ------------------------------------------------------------------------ results

app.MapGet("/api/configs", (ResultsService results) => Results.Json(results.ListConfigs()));

app.MapGet("/api/results", (ResultsService results) => Results.Json(results.ListRuns()));

app.MapGet("/api/runs/{timestamp}", (string timestamp, ResultsService results) =>
{
    object? run = results.GetRun(timestamp);
    return run is null ? Results.NotFound() : Results.Json(run);
});

app.MapGet("/api/runs/{timestamp}/transcripts", (string timestamp, ResultsService results) =>
    Results.Json(results.GetTranscripts(timestamp)));

app.Run();

/// <summary>
/// Per-run overrides applied on top of the selected config file. Any field left
/// blank/empty in the UI arrives as null here and is ignored — only set values
/// override the config. Numeric knobs use a > 0 guard, so 0/blank means "leave
/// the config value alone".
/// </summary>
public sealed record RunOverrides(
    string? BaseUrl,
    string? ApiKey,
    int? MaxTokens,
    int? RepeatsPerTask,
    double? Temperature,
    double? TopP,
    int? TopK,
    double? MinP,
    double? RepetitionPenalty,
    bool? EnableThinking,
    string? ReasoningEffort);

public sealed record RunRequest(string? ConfigPath, RunOverrides? Overrides);
