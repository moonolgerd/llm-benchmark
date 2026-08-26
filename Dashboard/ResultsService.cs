using System.Globalization;
using CsvHelper;

namespace LlmBenchmark.Dashboard;

/// <summary>
/// Scans the results/ directory next to the dashboard exe (the same place
/// RunController writes new runs) and turns the timestamped CSVs into JSON for
/// the /api/results and /api/runs/* endpoints. Old CLI-produced files parse
/// too, so historical runs are viewable without re-running.
/// </summary>
public sealed class ResultsService
{
    /// <summary>CSV datasets served by GET /api/runs/{timestamp} (transcripts have their own endpoint).</summary>
    private static readonly string[] CsvDatasets =
    {
        "speed", "context-probe",
        "agent-concurrency", "agent-concurrency-summary",
        "agent-workflow-stages", "agent-workflow-pipelines"
    };

    public static string BaseDir => AppContext.BaseDirectory;
    public static string ResultsDir => Path.Combine(AppContext.BaseDirectory, "results");

    // ------------------------------------------------------------------ configs

    public List<object> ListConfigs()
    {
        var list = new List<object>();
        if (Directory.Exists(BaseDir))
        {
            foreach (string file in Directory.EnumerateFiles(BaseDir, "*.json").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string name = Path.GetFileName(file);
                // Skip SDK-generated manifests that live next to the exe — they
                // are not benchmark configs.
                if (name.EndsWith(".deps.json") || name.EndsWith(".runtimeconfig.json") || name.Contains("staticwebassets"))
                    continue;
                list.Add(new { name, path = name });
            }
        }
        return list;
    }

    // -------------------------------------------------------------------- runs

    public List<object> ListRuns()
    {
        var byTimestamp = new SortedDictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (Directory.Exists(ResultsDir))
        {
            foreach (string file in Directory.EnumerateFiles(ResultsDir))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                // Expect "<dataset>-<yyyyMMdd-HHmmss>". Dataset names contain hyphens,
                // so split at the last hyphen before the fixed 15-char timestamp.
                if (name.Length < 16 || name[^16] != '-') continue;
                string ts = name[^15..];
                if (!TryParseTimestamp(ts)) continue;
                string dataset = name[..^16];
                if (!byTimestamp.TryGetValue(ts, out var datasets))
                    byTimestamp[ts] = datasets = new HashSet<string>();
                datasets.Add(dataset);
            }
        }

        return byTimestamp.Reverse()
            .Select(kv => (object)new
            {
                timestamp = kv.Key,
                datasets = kv.Value.OrderBy(d => d, StringComparer.Ordinal).ToArray()
            })
            .ToList();
    }

    public object? GetRun(string timestamp)
    {
        if (!TryParseTimestamp(timestamp)) return null;

        var run = new Dictionary<string, object?>();
        foreach (string dataset in CsvDatasets)
        {
            string path = Path.Combine(ResultsDir, $"{dataset}-{timestamp}.csv");
            if (!File.Exists(path)) continue;
            run[dataset] = ParseCsvFile(File.ReadAllText(path));
        }
        return run.Count > 0 ? (object)run : null;
    }

    public List<object> GetTranscripts(string timestamp)
    {
        var list = new List<object>();
        string path = Path.Combine(ResultsDir, $"quality-transcripts-{timestamp}.txt");
        if (!File.Exists(path)) return list;

        // Block format (see ResultsWriter.WriteQualityTranscripts):
        //   === ModelId :: TaskName :: attempt N ===
        //   <raw response text, possibly many lines>
        //   <blank line> / 80-dash separator / <blank line>
        string? modelId = null, taskName = null;
        int attempt = 0;
        var text = new System.Text.StringBuilder();

        void Flush()
        {
            if (modelId is null) return;
            list.Add(new { modelId, taskName, attempt, text = text.ToString().TrimEnd('\n') });
            text.Clear();
        }

        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("=== ") && line.EndsWith(" ==="))
            {
                Flush();
                // Strip the 4-char "=== " prefix and 4-char " ===" suffix.
                var parts = line[4..^4].Split(" :: ");
                modelId = parts.Length > 0 ? parts[0] : "";
                taskName = parts.Length > 1 ? parts[1] : "";
                attempt = parts.Length > 2 && int.TryParse(parts[2].Replace("attempt", "").Trim(), out int a) ? a : 0;
            }
            else if (line.StartsWith(new string('-', 40)))
            {
                // Separator line — skip.
            }
            else if (modelId is not null)
            {
                if (text.Length > 0) text.Append('\n');
                text.Append(line);
            }
        }
        Flush();
        return list;
    }

    // -------------------------------------------------------------------- csv

    /// <summary>
    /// Parses a result CSV into row objects keyed by the column header. Field
    /// splitting is CsvHelper's job (RFC-4180: quoted fields, embedded commas
    /// and newlines in Error columns); the typing below is ours:
    /// Success/Succeeded columns become booleans; a column whose non-empty cells
    /// all parse as invariant-culture doubles becomes numbers (non-finite values
    /// surface as null in JSON); everything else stays a string.
    /// </summary>
    private static List<Dictionary<string, object?>> ParseCsvFile(string text)
    {
        using var csv = new CsvReader(new StringReader(text), CultureInfo.InvariantCulture);
        // Read the first record as the header via the core API — deliberately not
        // ReadHeader(), which throws "No header record was found" on perfectly
        // valid files in CsvHelper 33.x.
        if (!csv.Read()) return new();
        string[] header = new string[csv.ColumnCount];
        for (int c = 0; c < csv.ColumnCount; c++)
            header[c] = csv.GetField(c) ?? "";
        if (header.Length == 0 || (header.Length == 1 && header[0].Length == 0)) return new();

        // Read all records up front (files are small) so column typing can look
        // at every cell before any row is emitted.
        var rows = new List<string[]>();
        while (csv.Read())
        {
            var cells = new string[header.Length];
            for (int c = 0; c < header.Length; c++)
                cells[c] = csv.GetField(c) ?? "";
            // Tolerate stray blank lines.
            if (cells.Length == 1 && cells[0].Length == 0) continue;
            rows.Add(cells);
        }

        var isBool = new bool[header.Length];
        var isNumber = new bool[header.Length];
        for (int c = 0; c < header.Length; c++)
        {
            if (header[c] == "Success" || header[c] == "Succeeded")
            {
                isBool[c] = true;
                continue;
            }
            isNumber[c] = true;
            foreach (var cells in rows)
            {
                if (cells[c].Length == 0) continue;
                if (!double.TryParse(cells[c], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    isNumber[c] = false;
                    break;
                }
            }
        }

        var result = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var cells in rows)
        {
            var row = new Dictionary<string, object?>();
            for (int c = 0; c < header.Length; c++)
            {
                string cell = cells[c];
                object? value;
                if (isBool[c])
                {
                    value = cell.Equals("true", StringComparison.OrdinalIgnoreCase);
                }
                else if (isNumber[c] && double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                {
                    // NaN/Infinity have no JSON representation — surface as null
                    // (the UI renders them as "n/a"), never as a crash.
                    value = double.IsFinite(d) ? d : null;
                }
                else
                {
                    value = cell;
                }
                row[header[c]] = value;
            }
            result.Add(row);
        }
        return result;
    }

    private static bool TryParseTimestamp(string ts) =>
        DateTime.TryParseExact(ts, "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
