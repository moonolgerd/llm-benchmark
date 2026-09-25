// Standalone dev tool: turns the article's run CSVs into publication PNGs.
// Run from anywhere with: dotnet run --project tools/ChartBuilder
// Not part of the shipped app (CLI/Dashboard/AppHost don't reference this project).

using System.Globalization;
using CsvHelper;
using ScottPlot;

const string RunTimestamp = "20260911-200157";
const string QwenModelId = "esatapedico/Qwen3.8-27B-NVFP4-MTP-GGUF:Qwen3.8-27B-NVFP4-MTP-HIGH";
const string OrnithModelId = "ornith-ai/Ornith-1.5-35B-A3B-GGUF:Q4_K_M";

var qwenColor = Color.FromHex("#2E6FDB");   // dense — blue
var ornithColor = Color.FromHex("#E8862B"); // MoE — orange

string repoRoot = FindRepoRoot();
string resultsDir = Path.Combine(repoRoot, "results");
string outDir = Path.Combine(repoRoot, "docs", "charts");
Directory.CreateDirectory(outDir);

var speedRows = LoadCsv<SpeedRow>(Path.Combine(resultsDir, $"speed-{RunTimestamp}.csv"))
    .Where(r => r.Success).ToList();
var contextRows = LoadCsv<ContextProbeRow>(Path.Combine(resultsDir, $"context-probe-{RunTimestamp}.csv"))
    .Where(r => r.Succeeded).ToList();
var concurrencyRows = LoadCsv<ConcurrencySummaryRow>(
    Path.Combine(resultsDir, $"agent-concurrency-summary-{RunTimestamp}.csv"));
var pipelineRows = LoadCsv<PipelineRow>(
    Path.Combine(resultsDir, $"agent-workflow-pipelines-{RunTimestamp}.csv")).Where(r => r.Success).ToList();
var qualityRows = LoadCsv<QualityRow>(Path.Combine(resultsDir, "quality-scores.csv"));

BuildDecodeTpsBar();
BuildPowerAndEfficiencyBars();
BuildMtpAcceptRateRange();
BuildPrefillTpsGroupedBar();
BuildContextVsVramLine();
BuildQualityGroupedBar();
BuildAgentConcurrencyScaling();
BuildWorkflowPipelineScaling();
BuildMtpOnOffComparison();

Console.WriteLine($"Wrote 10 charts to {outDir}");

// --------------------------------------------------------------------- helpers

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, "Core")) &&
            Directory.Exists(Path.Combine(dir.FullName, "results")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate repo root (expected Core/ and results/ siblings above the build output).");
}

static List<T> LoadCsv<T>(string path)
{
    using var reader = new StreamReader(path);
    using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
    return csv.GetRecords<T>().ToList();
}

static void StyleAndSave(Plot plot, string title, string yLabel, string fileName, string outDir, int w = 900, int h = 600)
{
    plot.Title(title);
    plot.Axes.Left.Label.Text = yLabel;
    plot.FigureBackground.Color = Colors.White;
    plot.DataBackground.Color = Colors.White;
    plot.SavePng(Path.Combine(outDir, fileName), w, h);
}

// --------------------------------------------------------------------- charts

void BuildDecodeTpsBar()
{
    var plot = new Plot();
    double qwenAvg = speedRows.Where(r => r.ModelId == QwenModelId).Average(r => r.TokensPerSecond);
    double ornithAvg = speedRows.Where(r => r.ModelId == OrnithModelId).Average(r => r.TokensPerSecond);
    double qwenRange = HalfRange(speedRows.Where(r => r.ModelId == QwenModelId).Select(r => r.TokensPerSecond));
    double ornithRange = HalfRange(speedRows.Where(r => r.ModelId == OrnithModelId).Select(r => r.TokensPerSecond));

    Bar[] bars =
    [
        new() { Position = 1, Value = qwenAvg, Error = qwenRange, FillColor = qwenColor },
        new() { Position = 2, Value = ornithAvg, Error = ornithRange, FillColor = ornithColor },
    ];
    plot.Add.Bars(bars);
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
        [new Tick(1, "Qwen3.8-27B\n(dense, NVFP4)"), new Tick(2, "Ornith-1.5-35B\n(MoE)")]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Margins(bottom: 0);
    plot.HideGrid();
    StyleAndSave(plot, "Decode Throughput (avg ± half-range)", "tokens / second", "decode-tps-bar.png", outDir);
}

void BuildPowerAndEfficiencyBars()
{
    {
        var plot = new Plot();
        double qwenW = speedRows.Where(r => r.ModelId == QwenModelId).Average(r => r.AvgPowerW);
        double ornithW = speedRows.Where(r => r.ModelId == OrnithModelId).Average(r => r.AvgPowerW);
        Bar[] bars =
        [
            new() { Position = 1, Value = qwenW, FillColor = qwenColor },
            new() { Position = 2, Value = ornithW, FillColor = ornithColor },
        ];
        plot.Add.Bars(bars);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            [new Tick(1, "Qwen3.8-27B"), new Tick(2, "Ornith-1.5-35B")]);
        plot.Axes.Bottom.MajorTickStyle.Length = 0;
        plot.Axes.Margins(bottom: 0);
        plot.HideGrid();
        StyleAndSave(plot, "Average Board Power Draw", "watts", "power-draw-bar.png", outDir);
    }
    {
        var plot = new Plot();
        double qwenJ = speedRows.Where(r => r.ModelId == QwenModelId).Average(r => r.TokensPerJoule);
        double ornithJ = speedRows.Where(r => r.ModelId == OrnithModelId).Average(r => r.TokensPerJoule);
        Bar[] bars =
        [
            new() { Position = 1, Value = qwenJ, FillColor = qwenColor },
            new() { Position = 2, Value = ornithJ, FillColor = ornithColor },
        ];
        plot.Add.Bars(bars);
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            [new Tick(1, "Qwen3.8-27B"), new Tick(2, "Ornith-1.5-35B")]);
        plot.Axes.Bottom.MajorTickStyle.Length = 0;
        plot.Axes.Margins(bottom: 0);
        plot.HideGrid();
        StyleAndSave(plot, "Decode Efficiency", "tokens / joule", "tokens-per-joule-bar.png", outDir);
    }
}

void BuildMtpAcceptRateRange()
{
    // No per-request draft-acceptance CSV exists (the benchmark tool doesn't capture
    // llama-server's internal log lines) — these ranges are read by hand from the
    // server log for this run, per docs/article-plan.md's decision log. Floating bars
    // (ValueBase..Value) rather than Add.Ranges so each keeps its model's chart color.
    var plot = new Plot();
    Bar[] bars =
    [
        new() { Position = 1, ValueBase = 93, Value = 97, FillColor = qwenColor },
        new() { Position = 2, ValueBase = 51.6, Value = 100, FillColor = ornithColor },
    ];
    plot.Add.Bars(bars);
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
        [new Tick(1, "Qwen3.8-27B\n(mean draft len ~2.9 tok)"), new Tick(2, "Ornith-1.5-35B\n(mean draft len 34-65 tok)")]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Bottom.Label.Text = "draft acceptance %  (session-level range from llama-server logs, not per-request data)";
    StyleAndSave(plot, "MTP Self-Speculation: Draft Acceptance Range", "", "mtp-accept-rate-range.png", outDir);
}

void BuildPrefillTpsGroupedBar()
{
    var plot = new Plot();
    long[] steps = [8192, 16384, 32768, 65536, 98304, 131072, 196608];
    var bars = new List<Bar>();
    var ticks = new List<Tick>();

    for (int i = 0; i < steps.Length; i++)
    {
        long step = steps[i];
        double groupCenter = i * 3 + 1.5;
        ticks.Add(new Tick(groupCenter, $"{step / 1024}K"));

        var qwenRow = contextRows.FirstOrDefault(r => r.ModelId == QwenModelId && r.RequestedContextTokens == step);
        var ornithRow = contextRows.FirstOrDefault(r => r.ModelId == OrnithModelId && r.RequestedContextTokens == step);

        if (qwenRow is not null)
            bars.Add(new Bar { Position = i * 3 + 1, Value = PrefillTps(qwenRow), FillColor = qwenColor });
        if (ornithRow is not null)
            bars.Add(new Bar { Position = i * 3 + 2, Value = PrefillTps(ornithRow), FillColor = ornithColor });
    }

    plot.Add.Bars(bars);
    plot.Legend.IsVisible = true;
    plot.Legend.Alignment = Alignment.UpperRight;
    plot.Legend.ManualItems.Add(new LegendItem { LabelText = "Qwen3.8-27B (dense) — no bar at 192K: exceeds its 180,480-tok device ceiling", FillColor = qwenColor });
    plot.Legend.ManualItems.Add(new LegendItem { LabelText = "Ornith-1.5-35B (MoE)", FillColor = ornithColor });
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. ticks]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Bottom.Label.Text = "requested context size";
    plot.Axes.Margins(bottom: 0);
    plot.HideGrid();
    StyleAndSave(plot, "Prefill Throughput vs. Context Size (context-probe-derived)", "tokens / second", "prefill-tps-grouped-bar.png", outDir);

    static double PrefillTps(ContextProbeRow r) => r.RequestedContextTokens / (r.TotalDurationMs / 1000.0);
}

void BuildContextVsVramLine()
{
    var plot = new Plot();

    double[] qwenX = [.. contextRows.Where(r => r.ModelId == QwenModelId).Select(r => (double)r.RequestedContextTokens / 1024)];
    double[] qwenY = [.. contextRows.Where(r => r.ModelId == QwenModelId).Select(r => r.VramUsedMbAfter / 1024.0)];
    double[] ornithX = [.. contextRows.Where(r => r.ModelId == OrnithModelId).Select(r => (double)r.RequestedContextTokens / 1024)];
    double[] ornithY = [.. contextRows.Where(r => r.ModelId == OrnithModelId).Select(r => r.VramUsedMbAfter / 1024.0)];

    var qwenScatter = plot.Add.Scatter(qwenX, qwenY);
    qwenScatter.Color = qwenColor;
    qwenScatter.LegendText = "Qwen3.8-27B (dense)";
    var ornithScatter = plot.Add.Scatter(ornithX, ornithY);
    ornithScatter.Color = ornithColor;
    ornithScatter.LegendText = "Ornith-1.5-35B (MoE)";

    plot.Legend.IsVisible = true;
    plot.Legend.Alignment = Alignment.LowerRight;
    plot.Axes.Bottom.Label.Text = "requested context size (K tokens)";
    StyleAndSave(plot, "VRAM Usage vs. Context Size (essentially flat — fixed KV-cache allocation)", "VRAM used (GB)", "context-vs-vram-line.png", outDir);
}

void BuildQualityGroupedBar()
{
    var plot = new Plot();
    string[] tasks = ["strict-json\n-schema", "csharp-refactor\n-di", "cross-file\n-reasoning", "long-context\n-needle", "agentic\n-tool-call"];
    string[] taskKeys = ["strict-json-schema", "csharp-refactor-di", "cross-file-reasoning", "long-context-needle", "agentic-tool-call"];
    var bars = new List<Bar>();
    var ticks = new List<Tick>();

    for (int i = 0; i < taskKeys.Length; i++)
    {
        double groupCenter = i * 3 + 1.5;
        ticks.Add(new Tick(groupCenter, tasks[i]));

        double qwenAvg = qualityRows.Where(r => r.ModelId == QwenModelId && r.TaskName == taskKeys[i]).Average(r => r.Score);
        double ornithAvg = qualityRows.Where(r => r.ModelId == OrnithModelId && r.TaskName == taskKeys[i]).Average(r => r.Score);

        bars.Add(new Bar { Position = i * 3 + 1, Value = qwenAvg, FillColor = qwenColor });
        bars.Add(new Bar { Position = i * 3 + 2, Value = ornithAvg, FillColor = ornithColor });
    }

    plot.Add.Bars(bars);
    plot.Legend.IsVisible = true;
    plot.Legend.Alignment = Alignment.LowerLeft;
    plot.Legend.ManualItems.Add(new LegendItem { LabelText = "Qwen3.8-27B (dense)", FillColor = qwenColor });
    plot.Legend.ManualItems.Add(new LegendItem { LabelText = "Ornith-1.5-35B (MoE)", FillColor = ornithColor });
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. ticks]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Margins(bottom: 0);
    plot.Axes.SetLimitsY(0, 110);
    plot.HideGrid();
    StyleAndSave(plot, "Quality Rubric Score by Task (avg of 3 attempts, 0-100)", "score", "quality-grouped-bar.png", outDir);
}

void BuildAgentConcurrencyScaling()
{
    var plot = new Plot();
    int[] levels = [1, 2, 4, 8];
    var tps = new List<double>();
    var ttft = new List<double>();

    foreach (int level in levels)
    {
        // The first x1 repeat had an anomalous 19.4s cold-start TTFT (see article-plan.md) — excluded here.
        var rows = concurrencyRows.Where(r => r.ConcurrencyLevel == level)
            .Where(r => !(level == 1 && r.Repeat == 1))
            .ToList();
        tps.Add(rows.Average(r => r.AggregateTokensPerSecond));
        ttft.Add(rows.Average(r => r.AvgTtftMs) / 1000.0);
    }

    double[] x = [.. levels.Select(l => (double)l)];
    var tpsScatter = plot.Add.Scatter(x, tps.ToArray());
    tpsScatter.Color = qwenColor;
    tpsScatter.LegendText = "Aggregate tokens/s (left axis)";

    var rightAxis = plot.Axes.AddRightAxis();
    rightAxis.Label.Text = "avg time-to-first-token (s)";
    var ttftScatter = plot.Add.Scatter(x, ttft.ToArray());
    ttftScatter.Color = ornithColor;
    ttftScatter.Axes.YAxis = rightAxis;
    ttftScatter.LegendText = "Avg TTFT, seconds (right axis)";

    plot.Legend.IsVisible = true;
    plot.Legend.Alignment = Alignment.UpperLeft;
    plot.Axes.Bottom.Label.Text = "concurrent agents";
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
        [.. levels.Select(l => new Tick(l, l.ToString()))]);
    StyleAndSave(plot, "Agent Concurrency: Throughput Scales, TTFT Grows With It", "tokens / second", "agent-concurrency-scaling.png", outDir);
}

void BuildWorkflowPipelineScaling()
{
    var plot = new Plot();
    int[] levels = [1, 2, 4];
    double[] x = [.. levels.Select(l => (double)l)];
    double[] y = [.. levels.Select(l => pipelineRows.Where(r => r.PipelineParallelCount == l).Average(r => r.TotalDurationMs) / 1000.0)];

    Bar[] bars = [.. levels.Select((l, i) => new Bar { Position = i + 1, Value = y[i], FillColor = qwenColor })];
    plot.Add.Bars(bars);
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
        [.. levels.Select((l, i) => new Tick(i + 1, $"{l} parallel\npipeline{(l > 1 ? "s" : "")}"))]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Margins(bottom: 0);
    plot.HideGrid();
    StyleAndSave(plot, "Planner→Coder→Reviewer: Wall-Clock vs. Concurrent Pipelines", "seconds", "workflow-pipeline-scaling.png", outDir);
}

void BuildMtpOnOffComparison()
{
    // Clean matched-pair run, 2026-09-15: same weights (esatapedico NVFP4-MTP), same
    // f16 KV cache, same tasks/sampling, only --speculative-type differs (off vs mtp).
    // See docs/article-plan.md decision log. Shown as % change from the off baseline
    // since the three metrics live on very different absolute scales.
    var offRows = LoadCsv<SpeedRow>(Path.Combine(resultsDir, "speed-20260914-234513.csv")).Where(r => r.Success).ToList();
    var onRows = LoadCsv<SpeedRow>(Path.Combine(resultsDir, "speed-20260914-234838.csv")).Where(r => r.Success).ToList();

    double offTps = offRows.Average(r => r.TokensPerSecond);
    double onTps = onRows.Average(r => r.TokensPerSecond);
    double offPower = offRows.Average(r => r.AvgPowerW);
    double onPower = onRows.Average(r => r.AvgPowerW);
    double offEff = offRows.Average(r => r.TokensPerJoule);
    double onEff = onRows.Average(r => r.TokensPerJoule);

    double PctChange(double off, double on) => (on - off) / off * 100.0;

    var plot = new Plot();
    Bar[] bars =
    [
        new() { Position = 1, Value = PctChange(offTps, onTps), FillColor = qwenColor },
        new() { Position = 2, Value = PctChange(offPower, onPower), FillColor = qwenColor },
        new() { Position = 3, Value = PctChange(offEff, onEff), FillColor = qwenColor },
    ];
    plot.Add.Bars(bars);
    plot.Add.HorizontalLine(0, color: Colors.Black);
    plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
    [
        new Tick(1, $"Decode tok/s\n{offTps:F1} -> {onTps:F1}"),
        new Tick(2, $"Avg power (W)\n{offPower:F1} -> {onPower:F1}"),
        new Tick(3, $"Tokens/joule\n{offEff:F3} -> {onEff:F3}"),
    ]);
    plot.Axes.Bottom.MajorTickStyle.Length = 0;
    plot.Axes.Margins(bottom: 0);
    plot.HideGrid();
    StyleAndSave(plot, "MTP On vs. Off: % Change from Off Baseline (matched weights + KV cache)", "% change", "mtp-on-off-comparison.png", outDir);
}

static double HalfRange(IEnumerable<double> values)
{
    var list = values.ToList();
    return (list.Max() - list.Min()) / 2.0;
}

// --------------------------------------------------------------------- CSV row models

sealed class SpeedRow
{
    public string ModelId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int Attempt { get; set; }
    public bool Success { get; set; }
    public double TtftMs { get; set; }
    public double TotalDurationMs { get; set; }
    public double TokensPerSecond { get; set; }
    public double AvgPowerW { get; set; }
    public double PeakPowerW { get; set; }
    public double TokensPerJoule { get; set; }
}

sealed class ContextProbeRow
{
    public string ModelId { get; set; } = "";
    public long RequestedContextTokens { get; set; }
    public long DeviceMaxContextTokens { get; set; }
    public bool Succeeded { get; set; }
    public double TotalDurationMs { get; set; }
    public double VramUsedMbAfter { get; set; }
}

sealed class ConcurrencySummaryRow
{
    public int ConcurrencyLevel { get; set; }
    public int Repeat { get; set; }
    public double WallClockMs { get; set; }
    public int SuccessCount { get; set; }
    public int FailCount { get; set; }
    public double AggregateTokensPerSecond { get; set; }
    public double AvgTtftMs { get; set; }
}

sealed class PipelineRow
{
    public int PipelineParallelCount { get; set; }
    public int Repeat { get; set; }
    public int PipelineIndex { get; set; }
    public double TotalDurationMs { get; set; }
    public bool Success { get; set; }
}

sealed class QualityRow
{
    public string ModelId { get; set; } = "";
    public string TaskName { get; set; } = "";
    public int Attempt { get; set; }
    public double Score { get; set; }
}
