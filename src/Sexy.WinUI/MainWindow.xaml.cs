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
    // Tile word / icon colors (light variants read well on the dark theme).
    private static readonly Brush WordGreen = new SolidColorBrush(Color.FromArgb(255, 108, 203, 95));
    private static readonly Brush WordAmber = new SolidColorBrush(Color.FromArgb(255, 255, 201, 77));
    private static readonly Brush WordRed = new SolidColorBrush(Color.FromArgb(255, 255, 153, 164));
    private static readonly Brush WordGrey = new SolidColorBrush(Color.FromArgb(255, 160, 160, 160));

    private readonly EngineService _engine = new();
    private TaskbarIcon? _tray;

    private bool _busy;
    private bool _refreshing;
    private bool _suppressStartupToggle;
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
        Title = "Sexy";
        AppWindow.Title = "Sexy";
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
        if (saved is not null && saved.Width >= 480 && saved.Height >= 360)
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
            AppWindow.Resize(new SizeInt32(740, 520));

            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
            var posX = area.WorkArea.X + (area.WorkArea.Width - 740) / 2;
            var posY = area.WorkArea.Y + (area.WorkArea.Height - 520) / 2;
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
        HideToTrayButton.IsEnabled = enabled;
        ScheduleTile.IsEnabled = enabled;
        ProxyTile.IsEnabled = enabled && (_isProxyOn || _canTurnOn);
    }

    private void SetProxyTile(bool canAct, string word, Brush wordBrush, string sub)
    {
        ProxyWord.Text = word;
        ProxyWord.Foreground = wordBrush;
        ProxyIcon.Foreground = wordBrush;
        ProxySub.Text = sub;
        ProxyTile.IsEnabled = canAct;
        ProxyTile.Opacity = canAct ? 1.0 : 0.75;
    }

    private void SetScheduleTile(string word, Brush wordBrush, string sub)
    {
        SchedWord.Text = word;
        SchedWord.Foreground = wordBrush;
        SchedIcon.Foreground = wordBrush;
        SchedSub.Text = sub;
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
            ValueServer.Text = server;
            ToolTipService.SetToolTip(ValueServer, server);

            var portText = Get("v2rayN port", string.Empty);
            bool portListening;
            if (string.IsNullOrWhiteSpace(portText))
            {
                // Missing data must never look healthy.
                portListening = false;
                portText = "unknown (not reported)";
                ValuePort.Foreground = WordRed;
            }
            else
            {
                portListening = !portText.TrimStart().StartsWith("NOT listening", StringComparison.OrdinalIgnoreCase);
                ValuePort.Foreground = portListening ? WordGreen : WordRed;
            }
            ValuePort.Text = portText;

            ValueSchedule.Text = settings is not null
                ? $"ON {settings.OnStart}-{settings.OnEnd}, {EngineService.FormatDays(settings.Days)}"
                : Get("Schedule");

            ValueNow.Text = Get("Schedule state now");

            var pausedText = Get("Paused", "no");
            _isPaused = pausedText.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
            if (_isPaused)
            {
                var since = _engine.GetPausedSince();
                pausedText = since.HasValue
                    ? $"yes, since {since.Value:yyyy-MM-dd HH:mm} ({EngineService.FormatAgo(since.Value)})"
                    : "yes";
                ValuePaused.Foreground = WordRed;
            }
            else
            {
                pausedText = "no";
                ValuePaused.Foreground = null;
            }
            ValuePaused.Text = pausedText;

            var nextRaw = Get("Next scheduled change", "unknown");
            ValueNext.Text = EngineService.FormatNext(nextRaw);
            ToolTipService.SetToolTip(ValueNext, nextRaw);

            try
            {
                ValueLastRun.Text = await _engine.GetScheduledTaskSummaryAsync();
            }
            catch
            {
                // A failed task query must not blank the rest of the card.
                ValueLastRun.Text = "task info unavailable";
            }

            ToolTipService.SetToolTip(ValueLastRun, ValueLastRun.Text);

            // ---------- Proxy tile ----------
            _canTurnOn = true;
            var hint = string.Empty;
            if (stateUpper.StartsWith("OTHER", StringComparison.Ordinal))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable while another proxy is configured. Clear it first.";
                SetProxyTile(false, "OTHER", WordRed, "another proxy is set");
            }
            else if (string.IsNullOrWhiteSpace(Get("v2rayN port", string.Empty)))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: the scheduler did not report the v2rayN port.";
                SetProxyTile(false, "OFF", WordGrey, "port unknown - ON blocked");
            }
            else if (!portListening)
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: v2rayN is not listening, so the proxy would break your connection.";
                SetProxyTile(false, "OFF", WordGrey, "v2rayN is not listening");
            }
            else if (_isProxyOn)
            {
                SetProxyTile(true, "ON", WordGreen, "tap to turn OFF");
            }
            else
            {
                SetProxyTile(true, "OFF", WordGrey, "tap to turn ON");
            }

            // ---------- Schedule tile ----------
            var nowRaw = Get("Schedule state now", string.Empty);
            bool insideWindow = nowRaw.StartsWith("On", StringComparison.OrdinalIgnoreCase);
            if (_isPaused)
            {
                SetScheduleTile("PAUSED", WordAmber, "tap to resume");
            }
            else if (insideWindow)
            {
                SetScheduleTile("ON", WordGreen, "tap to pause");
            }
            else
            {
                SetScheduleTile("OFF", WordGrey, "outside window - tap to pause");
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

            _lastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            _canTurnOn = false;
            _isProxyOn = false;
            _isPaused = false;

            UpdateTrayIcon("ERROR");
            SetProxyTile(false, "OFF", WordGrey, "status unavailable");
            SetScheduleTile("?", WordGrey, "status unavailable");

            ValueServer.Text = ValuePort.Text = ValueSchedule.Text = ValueNow.Text =
                ValuePaused.Text = ValueNext.Text = ValueLastRun.Text = "-";

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

    private async void ProxyTile_Click(object sender, RoutedEventArgs e) => await DoActionAsync(_isProxyOn ? "Off" : "On");
    private async void ScheduleTile_Click(object sender, RoutedEventArgs e) => await DoActionAsync(_isPaused ? "Resume" : "Pause");

    private void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressStartupToggle) return;
        StartupService.SetEnabled(StartupSwitch.IsOn);
    }

    public void HideToTray() => AppWindow.Hide();

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

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
            Width = 560,
            Height = Math.Max(180, Math.Min(380, available - 220))
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
