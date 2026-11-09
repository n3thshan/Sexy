using Windows.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sexy;

/// <summary>
/// The whole UI of the app: a small panel that opens next to the tray icon.
/// Refreshes on load (the popup is re-created per open), after any action,
/// and on the Refresh button. No background polling.
/// </summary>
public sealed partial class TrayPanel : UserControl
{
    private static readonly Brush WordGreen = new SolidColorBrush(Color.FromArgb(255, 108, 203, 95));
    private static readonly Brush WordAmber = new SolidColorBrush(Color.FromArgb(255, 255, 201, 77));
    private static readonly Brush WordRed = new SolidColorBrush(Color.FromArgb(255, 255, 153, 164));
    private static readonly Brush WordGrey = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160));

    private readonly EngineService _engine = new();

    private bool _busy;
    private bool _refreshing;
    private string? _lastActionError;
    private bool _suppressStartupToggle;
    private bool _suppressProxyToggle;
    private bool _suppressSchedToggle;
    private bool _canTurnOn = true;
    private bool _isProxyOn;
    private bool _isPaused;

    /// <summary>Raised by the panel when the engine reports the proxy state; the host repaints the tray icon.</summary>
    public event Action<string>? StateChanged;

    /// <summary>Raised when the user wants to quit the app (Exit button).</summary>
    public event Action? ExitRequested;

    public TrayPanel()
    {
        InitializeComponent();

        _suppressStartupToggle = true;
        StartupSwitch.IsOn = StartupService.IsEnabled();
        _suppressStartupToggle = false;
        StartupService.Heal();

        Loaded += async (_, _) => await UpdateStatusAsync();
    }

    /// <summary>Refresh everything from the engine (called on open and by the Refresh button).</summary>
    public Task RefreshAsync() => UpdateStatusAsync();

    /// <summary>Run an engine action by name (used by the tray menu).</summary>
    public Task TrayActionAsync(string action) => DoActionAsync(action);

    private void SetProxyRow(bool canAct, string word, Brush wordBrush, string hint)
    {
        ProxyWord.Text = word;
        ProxyWord.Foreground = wordBrush;
        ProxyHint.Text = hint;
        _suppressProxyToggle = true;
        ProxySwitch.IsOn = _isProxyOn;
        ProxySwitch.IsEnabled = canAct;
        _suppressProxyToggle = false;
        ProxyWord.Opacity = canAct || _isProxyOn ? 1.0 : 0.75;
    }

    private void SetSchedRow(string word, Brush wordBrush, string hint)
    {
        SchedWord.Text = word;
        SchedWord.Foreground = wordBrush;
        SchedHint.Text = hint;
        _suppressSchedToggle = true;
        SchedSwitch.IsOn = !_isPaused;
        _suppressSchedToggle = false;
    }

    private void SetControlsEnabled(bool enabled)
    {
        RefreshButton.IsEnabled = enabled;
        EditButton.IsEnabled = enabled;
        LogButton.IsEnabled = enabled;
        ExitButton.IsEnabled = enabled;
        SchedSwitch.IsEnabled = enabled;
        ProxySwitch.IsEnabled = enabled && (_isProxyOn || _canTurnOn);
    }

    private async Task UpdateStatusAsync()
    {
        if (_refreshing) return;

        _refreshing = true;
        SetControlsEnabled(false);

        try
        {
            var map = await _engine.GetStatusAsync();
            var settings = SettingsService.TryRead(_engine.EngineDir);

            string Get(string key, string fallback = "-") =>
                map.TryGetValue(key, out var value) ? value : fallback;

            string state = Get("Proxy");
            string stateUpper = state.ToUpperInvariant();
            _isProxyOn = stateUpper.StartsWith("ON", StringComparison.Ordinal);
            StateChanged?.Invoke(stateUpper);

            var server = Get("Server");
            var portText = Get("v2rayN port", string.Empty);
            bool portListening;
            if (string.IsNullOrWhiteSpace(portText))
            {
                portListening = false;
                portText = "port unknown";
            }
            else
            {
                portListening = !portText.TrimStart().StartsWith("NOT listening", StringComparison.OrdinalIgnoreCase);
            }

            var pausedText = Get("Paused", "no");
            _isPaused = pausedText.StartsWith("YES", StringComparison.OrdinalIgnoreCase);

            // ---------- Proxy row ----------
            _canTurnOn = true;
            var hint = string.Empty;
            var portShort = portListening ? $"{server} - listening" : $"{server} - NOT listening";
            if (stateUpper.StartsWith("OTHER", StringComparison.Ordinal))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable while another proxy is configured. Clear it first.";
                SetProxyRow(false, "OTHER", WordRed, "another proxy is set");
            }
            else if (string.IsNullOrWhiteSpace(Get("v2rayN port", string.Empty)))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: the scheduler did not report the v2rayN port.";
                SetProxyRow(false, "OFF", WordGrey, "port unknown - ON blocked");
            }
            else if (!portListening)
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: v2rayN is not listening, so the proxy would break your connection.";
                SetProxyRow(false, "OFF", WordGrey, portShort);
            }
            else if (_isProxyOn)
            {
                SetProxyRow(true, "ON", WordGreen, portShort);
            }
            else
            {
                SetProxyRow(true, "OFF", WordGrey, portShort);
            }

            // ---------- Schedule row ----------
            var nowRaw = Get("Schedule state now", string.Empty);
            bool insideWindow = nowRaw.StartsWith("On", StringComparison.OrdinalIgnoreCase);
            if (_isPaused)
            {
                string pausedHint;
                var since = _engine.GetPausedSince();
                pausedHint = since.HasValue
                    ? $"paused since {since.Value:yyyy-MM-dd HH:mm} ({EngineService.FormatAgo(since.Value)}) - tap to resume"
                    : "paused - tap to resume";
                SetSchedRow("PAUSED", WordAmber, pausedHint);
            }
            else
            {
                var schedHint = settings is not null
                    ? $"ON {settings.OnStart}-{settings.OnEnd}, {EngineService.FormatDays(settings.Days)}"
                    : Get("Schedule");
                SetSchedRow(insideWindow ? "ON" : "OFF",
                    insideWindow ? WordGreen : WordGrey,
                    schedHint + (insideWindow ? " - tap to pause" : " (outside window)"));
            }

            if (hint.Length > 0)
            {
                HintText.Text = hint;
                HintText.Visibility = Visibility.Visible;
            }
            else if (_lastActionError is not null)
            {
                HintText.Text = _lastActionError;
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                HintText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _canTurnOn = false;
            _isProxyOn = false;
            _isPaused = false;
            _lastActionError = null;

            SetProxyRow(false, "OFF", WordGrey, "status unavailable");
            SetSchedRow("?", WordGrey, "status unavailable");

            HintText.Text = "Could not read status: " + ex.Message;
            HintText.Visibility = Visibility.Visible;
        }
        finally
        {
            _refreshing = false;
            SetControlsEnabled(true);
        }
    }

    // ---------------------------------------------------------------------
    // Actions
    // ---------------------------------------------------------------------
    private async Task DoActionAsync(string action)
    {
        if (_busy || _refreshing) return;

        _busy = true;
        SetControlsEnabled(false);

        try
        {
            var result = await _engine.InvokeAsync(action);
            var text = !string.IsNullOrWhiteSpace(result.Output)
                ? result.Output.Trim()
                : !string.IsNullOrWhiteSpace(result.Error)
                    ? result.Error.Trim()
                    : "Done.";

            if (result.ExitCode != 0)
            {
                _lastActionError = text;
                HintText.Text = text;
                HintText.Visibility = Visibility.Visible;
            }
            else
            {
                _lastActionError = null;
            }
        }
        catch (Exception ex)
        {
            _lastActionError = ex.Message;
            HintText.Text = ex.Message;
            HintText.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            SetControlsEnabled(true);
        }

        await UpdateStatusAsync();
    }

    private async void ProxySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressProxyToggle) return;
        await DoActionAsync(_isProxyOn ? "Off" : "On");
    }

    private async void SchedSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSchedToggle) return;
        await DoActionAsync(_isPaused ? "Resume" : "Pause");
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await UpdateStatusAsync();

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;
        StartupService.SetEnabled(StartupSwitch.IsOn);
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ---------------------------------------------------------------------
    // Dialogs
    // ---------------------------------------------------------------------
    private ScrollViewer ScrollableDialogContent(UIElement content)
    {
        var maxHeight = 380;
        return new ScrollViewer
        {
            Content = content,
            MaxHeight = maxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.TryRead(_engine.EngineDir) ?? new ScheduleSettings();
        var view = new EditScheduleDialog();
        view.LoadFrom(settings);

        var dialog = new ContentDialog
        {
            Title = "Edit Schedule",
            Content = ScrollableDialogContent(view),
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (dialogSender, args) =>
        {
            if (!view.TryBuildSettings(out var updated))
            {
                args.Cancel = true;
                return;
            }

            try
            {
                SettingsService.Write(_engine.EngineDir, updated);
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                view.ShowError(ex.Message);
                return;
            }

            _ = UpdateStatusAsync();
        };

        await dialog.ShowAsync();
    }

    private async void LogButton_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            Text = _engine.ReadLogTail(200),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Width = 520,
            Height = 320
        };

        var dialog = new ContentDialog
        {
            Title = "proxy-schedule.log (last 200 lines)",
            Content = ScrollableDialogContent(box),
            PrimaryButtonText = "Refresh",
            SecondaryButtonText = "Close",
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // keep the dialog open
            box.Text = _engine.ReadLogTail(200);
        };

        await dialog.ShowAsync();
    }
}
