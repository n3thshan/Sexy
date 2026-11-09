using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sexy;

public sealed partial class EditScheduleDialog : UserControl
{
    private readonly Dictionary<CheckBox, string> _dayBoxes;

    public EditScheduleDialog()
    {
        InitializeComponent();
        _dayBoxes = new Dictionary<CheckBox, string>
        {
            [DayMonday] = "Monday",
            [DayTuesday] = "Tuesday",
            [DayWednesday] = "Wednesday",
            [DayThursday] = "Thursday",
            [DayFriday] = "Friday",
            [DaySaturday] = "Saturday",
            [DaySunday] = "Sunday"
        };
    }

    public void LoadFrom(ScheduleSettings settings)
    {
        OnStartBox.Text = settings.OnStart;
        OnEndBox.Text = settings.OnEnd;
        ProxyServerBox.Text = settings.ProxyServer;
        BypassBox.Text = settings.Bypass;
        ConfigPathBox.Text = settings.V2rayNConfigPath;
        RequirePortSwitch.IsOn = settings.RequirePort;

        var selected = new HashSet<string>(settings.Days, StringComparer.OrdinalIgnoreCase);
        foreach (var (box, name) in _dayBoxes)
        {
            box.IsChecked = selected.Contains(name);
        }
    }

    public void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ClearError()
    {
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Validates the fields and builds a ScheduleSettings. Returns false and shows an error when invalid.</summary>
    public bool TryBuildSettings(out ScheduleSettings settings)
    {
        settings = new ScheduleSettings();
        ClearError();

        var onStart = OnStartBox.Text.Trim();
        var onEnd = OnEndBox.Text.Trim();

        if (!SettingsService.IsValidTime(onStart))
        {
            ShowError("From: use HH:mm (24-hour clock), for example 07:00.");
            return false;
        }

        if (!SettingsService.IsValidTime(onEnd))
        {
            ShowError("Until: use HH:mm (24-hour clock), for example 24:00.");
            return false;
        }

        var days = _dayBoxes.Where(pair => pair.Key.IsChecked == true).Select(pair => pair.Value).ToList();
        if (days.Count == 0)
        {
            ShowError("Pick at least one day, otherwise the proxy never turns ON automatically.");
            return false;
        }

        var proxyServer = ProxyServerBox.Text.Trim();
        var bypass = BypassBox.Text.Trim();

        if (proxyServer.Length == 0)
        {
            ShowError("ProxyServer cannot be empty. Use 'auto' or an explicit address like 127.0.0.1:10808.");
            return false;
        }

        if (bypass.Length == 0)
        {
            ShowError("Bypass cannot be empty. Use 'auto' or an explicit bypass list.");
            return false;
        }

        settings = new ScheduleSettings
        {
            OnStart = onStart,
            OnEnd = onEnd,
            Days = days,
            ProxyServer = proxyServer,
            Bypass = bypass,
            V2rayNConfigPath = ConfigPathBox.Text.Trim(),
            RequirePort = RequirePortSwitch.IsOn
        };
        return true;
    }
}
