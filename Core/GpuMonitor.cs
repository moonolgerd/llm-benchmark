using System.Diagnostics;
using System.Globalization;

namespace LlmBenchmark;

/// <summary>
/// Reads GPU state by shelling out to nvidia-smi. Requires nvidia-smi to be
/// on PATH (standard with any recent NVIDIA driver install on Windows/Linux).
/// </summary>
public static class GpuMonitor
{
    public static (int usedMb, int totalMb) ReadVram()
    {
        var line = QueryFirstLine("memory.used,memory.total");
        if (line is null) return (-1, -1);

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return (-1, -1);

        if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int used) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int total))
            return (used, total);

        return (-1, -1);
    }

    /// <summary>
    /// One instantaneous power/clock/utilisation reading. Returns null if
    /// nvidia-smi is missing or reports "[N/A]" for any field (common on some
    /// laptop dGPUs), so callers can just skip the sample.
    /// </summary>
    public static GpuPowerSample? ReadPowerSample()
    {
        var line = QueryFirstLine("power.draw,utilization.gpu,clocks.sm");
        if (line is null) return null;

        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3) return null;

        if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double powerW) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double utilPct) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double smMhz))
            return new GpuPowerSample(powerW, utilPct, smMhz);

        return null;
    }

    private static string? QueryFirstLine(string queryFields)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = $"--query-gpu={queryFields} --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // If multiple GPUs are present, this takes the first line/device.
            return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }
        catch
        {
            // nvidia-smi missing or failed — caller treats null as "unknown".
            return null;
        }
    }
}

public readonly record struct GpuPowerSample(double PowerW, double UtilPct, double SmClockMhz);

/// <summary>
/// Aggregate of the power samples taken across one timed request.
/// <see cref="Empty"/> (all values &lt; 0) means nvidia-smi produced no usable
/// reading — downstream power/efficiency columns are then left blank.
/// </summary>
public readonly record struct GpuSampleStats(
    int SampleCount, double AvgPowerW, double PeakPowerW, double AvgUtilPct, double AvgSmClockMhz)
{
    public static GpuSampleStats Empty => new(0, -1, -1, -1, -1);
    public bool HasData => SampleCount > 0 && AvgPowerW > 0;
}

/// <summary>
/// Polls <see cref="GpuMonitor.ReadPowerSample"/> on a background loop between
/// <c>new</c> and <see cref="Stop"/>, so a generation call can be bracketed to
/// get its average/peak board power (and from that, tokens per joule). Cheap
/// enough for a benchmark — one nvidia-smi spawn per interval, no measurable
/// effect on generation throughput — but not a precision power meter.
/// </summary>
public sealed class GpuSampler : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly List<GpuPowerSample> _samples = new();
    private readonly object _gate = new();

    public GpuSampler(int intervalMs = 500)
    {
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                var s = GpuMonitor.ReadPowerSample();
                if (s is not null)
                {
                    lock (_gate) _samples.Add(s.Value);
                }
                try { await Task.Delay(intervalMs, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public GpuSampleStats Stop()
    {
        _cts.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }

        lock (_gate)
        {
            if (_samples.Count == 0) return GpuSampleStats.Empty;
            return new GpuSampleStats(
                SampleCount: _samples.Count,
                AvgPowerW: _samples.Average(x => x.PowerW),
                PeakPowerW: _samples.Max(x => x.PowerW),
                AvgUtilPct: _samples.Average(x => x.UtilPct),
                AvgSmClockMhz: _samples.Average(x => x.SmClockMhz));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
