using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Sexy;

public sealed class EngineResult
{
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
}

/// <summary>
/// Talks to the PowerShell engine (Sexy.ps1) exactly the way the old
/// WinForms window did. The engine stays the source of truth for state.
/// </summary>
public sealed class EngineService
{
    /// <summary>Folder containing Sexy.ps1 and the rest of the engine files.</summary>
    public string EngineDir { get; }

    public string ScriptPath => Path.Combine(EngineDir, "Sexy.ps1");
    public string LogPath => Path.Combine(EngineDir, "proxy-schedule.log");
    public string PausePath => Path.Combine(EngineDir, "paused.flag");
    public string SettingsPath => Path.Combine(EngineDir, "settings.psd1");

    public EngineService()
    {
        EngineDir = ResolveEngineDir();
    }

    public static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"WindowsPowerShell\v1.0\powershell.exe");

    private static string ResolveEngineDir()
    {
        // 1. Explicit environment override (any machine, any folder).
        var env = Environment.GetEnvironmentVariable("SEXY_ENGINE_DIR");

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(env)) candidates.Add(env);

        // 2. Folder containing this exe.
        candidates.Add(AppContext.BaseDirectory);

        // 3. Walk up a few parents (repo root when run from src/.../bin/...).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5 && dir.Parent != null; i++)
        {
            dir = dir.Parent;
            candidates.Add(dir.FullName);
        }

        // 4. The known project folder on this machine.
        candidates.Add(@"C:\Users\Admin\Documents\Projects\v2rayN-proxy-schedule");

        foreach (var c in candidates)
        {
            if (!string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "Sexy.ps1")))
            {
                return c;
            }
        }

        // Fall back to the exe folder; the engine call will fail with a clear message.
        return AppContext.BaseDirectory;
    }

    /// <summary>Runs the engine with the given action. Same contract as the old window.</summary>
    public async Task<EngineResult> InvokeAsync(string action)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\" -Action {action}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the scheduler.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            throw new InvalidOperationException("The scheduler did not respond within 20 seconds.");
        }

        return new EngineResult
        {
            ExitCode = process.ExitCode,
            Output = await stdoutTask.ConfigureAwait(false),
            Error = await stderrTask.ConfigureAwait(false)
        };
    }

    /// <summary>Runs `-Action Status` and parses the `Key: value` lines into a map.</summary>
    public async Task<Dictionary<string, string>> GetStatusAsync()
    {
        var result = await InvokeAsync("Status").ConfigureAwait(false);
        var text = (result.Output ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            var reason = string.IsNullOrWhiteSpace(result.Error)
                ? "the scheduler returned no status"
                : result.Error.Trim();
            throw new InvalidOperationException(reason);
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line, @"^(?<key>[^:]+):\s*(?<value>.*)$");
            if (m.Success)
            {
                map[m.Groups["key"].Value.Trim()] = m.Groups["value"].Value.Trim();
            }
        }

        if (!map.ContainsKey("Proxy"))
        {
            throw new InvalidOperationException("the scheduler output was not understood");
        }

        return map;
    }

    /// <summary>Reads the paused.flag marker; returns when the schedule was paused.</summary>
    public DateTime? GetPausedSince()
    {
        try
        {
            if (!File.Exists(PausePath)) return null;
            var raw = File.ReadAllText(PausePath);
            var m = Regex.Match(raw, @"(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})");
            if (m.Success)
            {
                if (DateTime.TryParseExact(
                        m.Groups[1].Value,
                        "yyyy-MM-dd HH:mm:ss",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var parsed))
                {
                    return parsed;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Read-only look at the scheduled task (last run / last result / missed runs),
    /// via the small TaskInfo.ps1 helper shipped next to the exe.
    /// </summary>
    public async Task<string> GetScheduledTaskSummaryAsync()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "TaskInfo.ps1");
        if (!File.Exists(script)) return "task info unavailable (helper missing)";

        var psi = new ProcessStartInfo
        {
            FileName = PowerShellPath,
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        string output;
        using var process = Process.Start(psi);
        if (process == null) return "task info unavailable (could not query Task Scheduler)";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            output = (await stdout.ConfigureAwait(false)).Trim();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            return "task info unavailable (timeout)";
        }

        if (output.Length == 0 || output.Equals("NOTFOUND", StringComparison.OrdinalIgnoreCase))
        {
            return "task not found";
        }

        var parts = output.Split('|');
        if (parts.Length < 4) return "task info unavailable";

        var lastRunText = parts[0].Trim();
        if (!long.TryParse(parts[1].Trim(), out var rawCode))
        {
            return $"{lastRunText} - unknown result";
        }

        string resultText = rawCode switch
        {
            0 => "OK",
            267009 => "still running",
            267011 => "has not run yet",
            _ => $"0x{(uint)(rawCode & 0xFFFFFFFFL):X8}"
        };

        var text = $"{lastRunText} - {resultText}";

        if (DateTime.TryParseExact(
                lastRunText,
                "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var lastRun))
        {
            text += $" ({FormatAgo(lastRun)})";
        }

        if (int.TryParse(parts[2].Trim(), out var missed) && missed > 0)
        {
            text += $" - {missed} missed";
        }

        return text;
    }

    public string ReadLogTail(int maxLines)
    {
        try
        {
            if (!File.Exists(LogPath)) return "The log file does not exist yet.";
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var all = reader.ReadToEnd();
            var lines = all.Split('\n');
            if (lines.Length <= maxLines) return all.TrimEnd();
            return string.Join("\n", lines[^maxLines..]).TrimEnd();
        }
        catch (Exception ex)
        {
            return "Could not read the log: " + ex.Message;
        }
    }

    // --- formatting helpers (ported from the old window) ---

    public static string FormatAgo(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalSeconds < 0) return "in the future";
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)Math.Floor(span.TotalMinutes)} min ago";
        if (span.TotalHours < 24) return $"{(int)Math.Floor(span.TotalHours)}h {span.Minutes}m ago";
        return $"{(int)Math.Floor(span.TotalDays)}d {span.Hours}h ago";
    }

    public static string FormatNext(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown";

        if (!DateTime.TryParseExact(
                text.Trim(),
                "yyyy-MM-dd HH:mm",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out var parsed))
        {
            return text;
        }

        var span = parsed - DateTime.Now;
        if (span.TotalSeconds <= 0) return $"{text} (due now)";
        if (span.TotalMinutes < 1) return $"{text} (in under a minute)";
        if (span.TotalHours < 1) return $"{text} (in {(int)Math.Floor(span.TotalMinutes)} min)";
        if (span.TotalDays < 1) return $"{text} (in {(int)Math.Floor(span.TotalHours)}h {span.Minutes}m)";
        return $"{text} (in {(int)Math.Floor(span.TotalDays)}d {span.Hours}h)";
    }

    private static readonly Dictionary<string, string> ShortDay = new()
    {
        ["Monday"] = "Mon", ["Tuesday"] = "Tue", ["Wednesday"] = "Wed", ["Thursday"] = "Thu",
        ["Friday"] = "Fri", ["Saturday"] = "Sat", ["Sunday"] = "Sun"
    };

    private static readonly string[] AllDays =
        { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday" };

    public static string FormatDays(IEnumerable<string> days)
    {
        var set = new HashSet<string>(days.Select(d => d.Trim()), StringComparer.OrdinalIgnoreCase);

        if (set.Count == 0) return "no days";
        if (set.Count == 7) return "every day";

        var weekdays = new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" };
        if (set.Count == 5 && weekdays.All(set.Contains)) return "Mon-Fri";

        var names = AllDays.Where(set.Contains).Select(d => ShortDay[d]);
        return string.Join(", ", names);
    }
}
