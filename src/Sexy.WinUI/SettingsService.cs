using System.Text;
using System.Text.RegularExpressions;

namespace Sexy;

public sealed class ScheduleSettings
{
    public string OnStart { get; set; } = "07:00";
    public string OnEnd { get; set; } = "24:00";
    public List<string> Days { get; set; } = new();
    public string ProxyServer { get; set; } = "auto";
    public string Bypass { get; set; } = "auto";
    public string V2rayNConfigPath { get; set; } = "";
    public bool RequirePort { get; set; } = true;
}

/// <summary>
/// Reads and writes settings.psd1. The engine uses Import-PowerShellDataFile,
/// so the file must stay a valid PowerShell data file. The writer reproduces
/// the same layout and comments as the original file, and always keeps a
/// one-shot backup in settings.psd1.bak before overwriting.
/// </summary>
public static class SettingsService
{
    private static readonly Regex TimePattern = new(
        @"^((0[0-9]|1[0-9]|2[0-3]):[0-5][0-9]|24:00)$",
        RegexOptions.Compiled);

    public static bool IsValidTime(string value) => TimePattern.IsMatch(value.Trim());

    private static string? MatchGroup(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    public static ScheduleSettings? TryRead(string engineDir)
    {
        var path = Path.Combine(engineDir, "settings.psd1");
        if (!File.Exists(path)) return null;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var settings = new ScheduleSettings
        {
            OnStart = MatchGroup(text, @"OnStart\s*=\s*'([^']*)'") ?? "07:00",
            OnEnd = MatchGroup(text, @"OnEnd\s*=\s*'([^']*)'") ?? "24:00",
            ProxyServer = MatchGroup(text, @"ProxyServer\s*=\s*'([^']*)'") ?? "auto",
            Bypass = MatchGroup(text, @"Bypass\s*=\s*'([^']*)'") ?? "auto",
            V2rayNConfigPath = MatchGroup(text, @"V2rayNConfigPath\s*=\s*'([^']*)'") ?? ""
        };

        var daysBody = MatchGroup(text, @"Days\s*=\s*@\((?<body>[^)]*)\)") ?? string.Empty;
        settings.Days = Regex.Matches(daysBody, @"'([^']+)'")
            .Cast<Match>()
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        var requireMatch = Regex.Match(text, @"RequirePort\s*=\s*\$(?<v>true|false)", RegexOptions.IgnoreCase);
        settings.RequirePort = !requireMatch.Success || requireMatch.Groups["v"].Value.Equals("true", StringComparison.OrdinalIgnoreCase);

        return settings;
    }

    private static string EscapePsString(string value) => value.Replace("'", "''");

    public static void Write(string engineDir, ScheduleSettings s)
    {
        var path = Path.Combine(engineDir, "settings.psd1");
        var bakPath = path + ".bak";

        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, bakPath, overwrite: true);
            }
        }
        catch
        {
            // Losing the backup must not block a legitimate edit; the write below may still succeed.
        }

        var days = string.Join(
            "\n",
            s.Days.Select(d => $"        '{EscapePsString(d)}'"));

        var requirePort = s.RequirePort ? "$true" : "$false";

        var text =
            "@{\n" +
            "    # Proxy ON window, local time, 24-hour clock.\n" +
            "    # \"24:00\" means midnight at the end of the day.\n" +
            $"    OnStart = '{EscapePsString(s.OnStart)}'\n" +
            $"    OnEnd   = '{EscapePsString(s.OnEnd)}'\n" +
            "\n" +
            "    # Days when the ON window applies.\n" +
            "    Days = @(\n" +
            days + "\n" +
            "    )\n" +
            "\n" +
            "    # 'auto' reads the port, proxy address and bypass list from v2rayN.\n" +
            "    # You can also put an explicit value, for example '127.0.0.1:10808'.\n" +
            $"    ProxyServer = '{EscapePsString(s.ProxyServer)}'\n" +
            $"    Bypass      = '{EscapePsString(s.Bypass)}'\n" +
            "\n" +
            "    # Leave empty to use the normal v2rayN install path.\n" +
            $"    V2rayNConfigPath = '{EscapePsString(s.V2rayNConfigPath)}'\n" +
            "\n" +
            "    # Do not turn the proxy ON unless the v2rayN proxy port is listening.\n" +
            $"    RequirePort = {requirePort}\n" +
            "}\n";

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
