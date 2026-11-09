using Microsoft.Win32;

namespace Sexy;

/// <summary>
/// Per-user "Start with Windows" entry in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// No admin rights involved. The value points at this exe with the "--tray"
/// argument so the app starts hidden to the tray.
/// </summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sexy";

    private static string Command => "\"" + Environment.ProcessPath + "\" --tray";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var value = key?.GetValue(ValueName) as string;
            return value is not null && value.Contains("Sexy.WinUI.exe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                // Always write the current exe path - repairs broken/moved entries.
                key?.SetValue(ValueName, Command);
            }
            else
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry access can fail (locked-down profile); the switch just won't persist.
        }
    }

    /// <summary>If the entry exists, rewrite it to this exe's current path (survives updates).</summary>
    public static void Heal()
    {
        if (IsEnabled())
        {
            SetEnabled(true);
        }
    }
}
