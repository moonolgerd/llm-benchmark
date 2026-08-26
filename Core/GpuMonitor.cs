using System.Diagnostics;
using System.Globalization;

namespace LlmBenchmark;

/// <summary>
/// Reads VRAM usage by shelling out to nvidia-smi. Requires nvidia-smi to be
/// on PATH (standard with any recent NVIDIA driver install on Windows/Linux).
/// </summary>
public static class GpuMonitor
{
    public static (int usedMb, int totalMb) ReadVram()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, -1);

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            // If multiple GPUs are present, this takes the first line/device.
            // Adjust the index below if you need a specific GPU.
            var firstLine = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstLine is null) return (-1, -1);

            var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2) return (-1, -1);

            int used = int.Parse(parts[0], CultureInfo.InvariantCulture);
            int total = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return (used, total);
        }
        catch
        {
            // nvidia-smi missing or failed — caller should treat -1 as "unknown"
            return (-1, -1);
        }
    }
}
