using Windows.UI;
using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Sexy;

public sealed partial class MainWindow : Window
{
    private readonly EngineService _engine = new();
    private TaskbarIcon? _tray;

    private bool _busy;
    private bool _refreshing;
    private bool _suppressStartupToggle;
    private bool _suppressProxyToggle;
    private bool _suppressSchedToggle;
    private string? _lastActionError;
    private bool _exiting;
    private bool _canTurnOn = true;
    private bool _isProxyOn;
    private bool _isPaused;
    private DateTime _lastRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        Title = "Sexy Proxy";
        AppWindow.Title = "Sexy Proxy";
        SystemBackdrop = new MicaBackdrop();

        if (AppTitleBar is not null)
        {
            SetTitleBar(AppTitleBar);
        }

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }

        var saved = WindowState.Load();
        if (saved is not null && saved.Width >= 340 && saved.Height >= 480)
        {
            AppWindow.Resize(new SizeInt32(saved.Width, saved.Height));

            // Restore the position, but keep the window on-screen.
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var x = Math.Clamp(saved.X, area.WorkArea.X,
                Math.Max(area.WorkArea.X, area.WorkArea.X + area.WorkArea.Width - saved.Width));
            var y = Math.Clamp(saved.Y, area.WorkArea.Y,
                Math.Max(area.WorkArea.Y, area.WorkArea.Y + area.WorkArea.Height - saved.Height));
            AppWindow.Move(new PointInt32(x, y));
        }
        else
        {
            AppWindow.Resize(new SizeInt32(440, 720));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var posX = area.WorkArea.X + (area.WorkArea.Width - 440) / 2;
            var posY = area.WorkArea.Y + (area.WorkArea.Height - 720) / 2;
            AppWindow.Move(new PointInt32(posX, posY));
        }

        AppWindow.Changed += (s, args) =>
        {
            if (args.DidSizeChange || args.DidPositionChange)
            {
                WindowState.Save(AppWindow);
            }
        };

        _suppressStartupToggle = true;
        StartupSwitch.IsOn = StartupService.IsEnabled();
        _suppressStartupToggle = false;
        StartupService.Heal();
        StartupOnOff.Text = StartupSwitch.IsOn ? "On" : "Off";

        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        CreateTrayIcon();

        RootGrid.Loaded += async (_, _) => await UpdateStatusAsync();
    }

    // ---------------------------------------------------------------------
    // Tray icon (H.NotifyIcon.WinUI). The Win32 popup menu calls commands,
    // not Click handlers, so every item gets an ICommand. The icon is the
    // Fluent shield; tinted green while the proxy is ON.
    // ---------------------------------------------------------------------
    private void CreateTrayIcon()
    {
        static MenuFlyoutItem Item(string text, Action action) => new()
        {
            Text = text,
            Command = new RelayCommand(action)
        };

        var menu = new MenuFlyout();
        menu.Items.Add(Item("Open Sexy", ShowFromTray));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Turn Proxy ON", () => TrayAction("On")));
        menu.Items.Add(Item("Turn Proxy OFF", () => TrayAction("Off")));
        menu.Items.Add(Item("Pause Schedule", () => TrayAction("Pause")));
        menu.Items.Add(Item("Resume Schedule", () => TrayAction("Resume")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Exit", ExitApp));

        _tray = new TaskbarIcon
        {
            ToolTipText = "Sexy - v2rayN schedule",
            NoLeftClickDelay = true
        };
        _tray.IconSource = TrayIcon(proxyOn: false);
        _tray.ContextFlyout = menu;
        _tray.LeftClickCommand = new RelayCommand(ToggleWindowFromTray);

        RootGrid.Children.Add(_tray);

        // The Loaded handler creates the native icon; a window that never
        // activates (e.g. --tray start) must not rely on that path alone.
        try
        {
            _tray.ForceCreate(false);
        }
        catch
        {
            // Tray creation hiccup (e.g. explorer restarting); the Loaded path will retry.
        }
    }

    // Plain Fluent shield glyph, drawn synchronously - this is the only tray
    // rendering path that does not crash the native icon pipeline. A file
    // based BitmapImage made the app die on launch (0xC000027B in
    // CoreMessagingXP.dll), so the single glyph is used and the ON state is
    // shown as the same glyph tinted green.
    private static GeneratedIconSource TrayIcon(bool proxyOn)
    {
        return new GeneratedIconSource
        {
            Text = "\uEA18", // Shield
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 46,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(proxyOn
                ? Color.FromArgb(255, 108, 203, 95)
                : Color.FromArgb(255, 245, 245, 245))
        };
    }

    private void UpdateTrayIcon(string stateUpper)
    {
        if (_tray is null) return;

        bool proxyOn = stateUpper.StartsWith("ON", StringComparison.Ordinal);
        _tray.IconSource = TrayIcon(proxyOn);
        _tray.ToolTipText = proxyOn
            ? "Sexy - Proxy ON"
            : stateUpper.StartsWith("OTHER", StringComparison.Ordinal)
                ? "Sexy - Another proxy is configured"
                : stateUpper == "ERROR"
                    ? "Sexy - Status unknown"
                    : "Sexy - Proxy OFF";
    }

    private void ToggleWindowFromTray()
    {
        if (AppWindow.IsVisible)
        {
            AppWindow.Hide();
        }
        else
        {
            ShowFromTray();
        }
    }

    public void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void TrayAction(string action)
    {
        // Tray actions run hidden and silent - no balloons, no opening the window.
        _ = DoActionAsync(action);
    }

    private void ExitApp()
    {
        _exiting = true;
        Application.Current.Exit();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;

        // The X button hides to the tray instead of quitting; Exit lives in the tray menu.
        args.Cancel = true;
        AppWindow.Hide();
    }

    private async void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated) return;
        if (_busy || _refreshing) return;
        if ((DateTime.Now - _lastRefresh).TotalSeconds < 5) return;

        await UpdateStatusAsync();
    }

    // ---------------------------------------------------------------------
    // Status refresh
    // ---------------------------------------------------------------------
    private void SetControlsEnabled(bool enabled)
    {
        EditButton.IsEnabled = enabled;
        LogButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
        SchedSwitch.IsEnabled = enabled;
        ProxySwitch.IsEnabled = enabled && (_isProxyOn || _canTurnOn);
    }

    private void SetProxyRow(bool canAct, string word, string sub)
    {
        ProxyOnOff.Text = word;
        ProxySub.Text = sub;
        _suppressProxyToggle = true;
        ProxySwitch.IsOn = _isProxyOn;
        _suppressProxyToggle = false;

        ProxySwitch.IsEnabled = canAct;
        ProxySwitch.Opacity = canAct ? 1.0 : 0.5;
    }

    private void SetSchedRow(string word, string sub, string schedText)
    {
        SchedOnOff.Text = word;
        SchedSub.Text = sub;
        ScheduleSub.Text = schedText;

        _suppressSchedToggle = true;
        SchedSwitch.IsOn = !_isPaused;
        _suppressSchedToggle = false;
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
            UpdateTrayIcon(stateUpper);

            var server = Get("Server");
            var portText = Get("v2rayN port", string.Empty);
            bool portListening;
            if (string.IsNullOrWhiteSpace(portText))
            {
                // Missing data must never look healthy.
                portListening = false;
            }
            else
            {
                portListening = !portText.TrimStart().StartsWith("NOT listening", StringComparison.OrdinalIgnoreCase);
            }

            var pausedText = Get("Paused", "no");
            _isPaused = pausedText.StartsWith("YES", StringComparison.OrdinalIgnoreCase);

            // Window line shown in 12-hour format, e.g. "ON 7:00 AM-12:00 AM, every day"
            var schedHint = settings is not null
                ? $"ON {EngineService.To12Hour(settings.OnStart)}-{EngineService.To12Hour(settings.OnEnd)}, {EngineService.FormatDays(settings.Days)}"
                : Get("Schedule");

            // Compact subline for the proxy row: server + port health
            var proxySub = string.IsNullOrWhiteSpace(portText)
                ? "port unknown - ON blocked"
                : portListening
                    ? $"v2rayN listening on {server}"
                    : "v2rayN is not listening";

            // ---------- Proxy tile ----------
            _canTurnOn = true;
            var hint = string.Empty;
            if (stateUpper.StartsWith("OTHER", StringComparison.Ordinal))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable while another proxy is configured. Clear it first.";
                SetProxyRow(false, "Off", "another proxy is set");
            }
            else if (string.IsNullOrWhiteSpace(Get("v2rayN port", string.Empty)))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: the scheduler did not report the v2rayN port.";
                SetProxyRow(false, "Off", proxySub);
            }
            else if (!portListening)
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: v2rayN is not listening, so the proxy would break your connection.";
                SetProxyRow(false, "Off", proxySub);
            }
            else if (_isProxyOn)
            {
                SetProxyRow(true, "On", proxySub);
            }
            else
            {
                SetProxyRow(true, "Off", proxySub);
            }

            // ---------- Schedule tile ----------
            var nowRaw = Get("Schedule state now", string.Empty);
            bool insideWindow = nowRaw.StartsWith("On", StringComparison.OrdinalIgnoreCase);
            if (_isPaused)
            {
                SetSchedRow("Off", "paused - tap to resume", schedHint + " - paused");
            }
            else if (insideWindow)
            {
                SetSchedRow("On", "inside the window - tap to pause", schedHint);
            }
            else
            {
                SetSchedRow("Off", "outside the schedule window", schedHint);
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

            try
            {
                RefreshSub.Text = "Last check " + await _engine.GetScheduledTaskSummaryAsync();
            }
            catch
            {
                RefreshSub.Text = "last check unavailable";
            }

            _lastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            _canTurnOn = false;
            _isProxyOn = false;
            _isPaused = false;

            UpdateTrayIcon("ERROR");
            SetProxyRow(false, "Off", "status unavailable");
            SetSchedRow("Off", "status unavailable", "-");

            // Hard errors surface as a thin red line instead of a banner.
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
        if (_busy || _refreshing)
        {
            return;
        }

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
                // Show the engine's refusal inline as a thin red line.
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

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;
        StartupService.SetEnabled(StartupSwitch.IsOn);
        StartupOnOff.Text = StartupSwitch.IsOn ? "On" : "Off";
    }

    public void HideToTray() => AppWindow.Hide();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await UpdateStatusAsync();
    }

    // Dialog content scrolls when the window is too short instead of clipping.
    private ScrollViewer ScrollableDialogContent(UIElement content)
    {
        var available = RootGrid.XamlRoot?.Size.Height ?? 700;
        var maxHeight = Math.Max(180, available - 170);
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
            XamlRoot = RootGrid.XamlRoot
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
        var available = RootGrid.XamlRoot?.Size.Height ?? 700;
        var box = new TextBox
        {
            Text = _engine.ReadLogTail(200),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Width = 340,
            Height = Math.Max(180, Math.Min(320, available - 220))
        };

        var dialog = new ContentDialog
        {
            Title = "proxy-schedule.log (last 200 lines)",
            Content = ScrollableDialogContent(box),
            PrimaryButtonText = "Refresh",
            SecondaryButtonText = "Close",
            XamlRoot = RootGrid.XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // keep the dialog open
            box.Text = _engine.ReadLogTail(200);
        };

        await dialog.ShowAsync();
    }
}
