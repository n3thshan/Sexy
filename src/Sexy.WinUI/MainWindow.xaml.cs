using System.Text.RegularExpressions;
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
    private bool _exiting;
    private bool _canTurnOn = true;
    private bool _isProxyOn;
    private bool _isPaused;
    private bool _lastError;
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

        AppWindow.Resize(new SizeInt32(740, 520));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var posX = area.WorkArea.X + (area.WorkArea.Width - 740) / 2;
        var posY = area.WorkArea.Y + (area.WorkArea.Height - 520) / 2;
        AppWindow.Move(new PointInt32(posX, posY));

        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        CreateTrayIcon();

        RootGrid.Loaded += async (_, _) => await UpdateStatusAsync();
    }

    // ---------------------------------------------------------------------
    // Tray icon (H.NotifyIcon.WinUI). The Win32 popup menu calls commands,
    // not Click handlers, so every item gets an ICommand. The icon is the
    // Fluent-style shield with a tick; a small green badge dot appears
    // bottom-right while the proxy is ON.
    // ---------------------------------------------------------------------
    private void CreateTrayIcon()
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Open Sexy",
            Command = new RelayCommand(ShowFromTray)
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Turn Proxy ON",
            Command = new RelayCommand(() => TrayAction("On"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Turn Proxy OFF",
            Command = new RelayCommand(() => TrayAction("Off"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Pause Schedule",
            Command = new RelayCommand(() => TrayAction("Pause"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Resume Schedule",
            Command = new RelayCommand(() => TrayAction("Resume"))
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Exit",
            Command = new RelayCommand(ExitApp)
        });

        _tray = new TaskbarIcon
        {
            ToolTipText = "Sexy - v2rayN schedule",
            NoLeftClickDelay = true
        };
        _tray.IconSource = TrayIcon(proxyOn: false);
        _tray.ContextFlyout = menu;
        _tray.LeftClickCommand = new RelayCommand(ToggleWindowFromTray);

        RootGrid.Children.Add(_tray);
    }

    // Shield with a tick, drawn synchronously - this is the only tray
    // rendering path that does not crash the native icon pipeline. A file
    // based BitmapImage made the app die on launch (0xC000027B in
    // CoreMessagingXP.dll), so the glyph pair is used and the ON state is
    // shown as the same glyphs tinted green.
    private static GeneratedIconSource TrayIcon(bool proxyOn)
    {
        return new GeneratedIconSource
        {
            Text = "\uEA18\uE73E", // Shield + CheckMark
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 36,
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
        _tray.ToolTipText = stateUpper.StartsWith("ON", StringComparison.Ordinal)
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

    private void ShowFromTray()
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
        _lastError = false;
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

            ValueLastRun.Text = await _engine.GetScheduledTaskSummaryAsync();
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

            HintText.Text = hint;
            HintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _lastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            _lastError = true;
            _canTurnOn = false;
            _isProxyOn = false;
            _isPaused = false;

            UpdateTrayIcon("ERROR");
            SetProxyTile(false, "OFF", WordGrey, "status unavailable");
            SetScheduleTile("?", WordGrey, "status unavailable");

            ValueServer.Text = ValuePort.Text = ValueSchedule.Text = ValueNow.Text =
                ValuePaused.Text = ValueNext.Text = ValueLastRun.Text = "-";

            HintText.Visibility = Visibility.Collapsed;
            ShowResult($"Could not read status: {ex.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            _refreshing = false;
            SetControlsEnabled(true);
        }
    }

    private void ShowResult(string text, InfoBarSeverity severity)
    {
        ResultBar.Title = severity switch
        {
            InfoBarSeverity.Error => "Error",
            InfoBarSeverity.Success => "Done",
            _ => "Info"
        };
        ResultBar.Message = text;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
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

            InfoBarSeverity severity;
            if (result.ExitCode != 0)
            {
                severity = InfoBarSeverity.Error;
            }
            else if (Regex.IsMatch(text, "not changed|already|Nothing to do", RegexOptions.IgnoreCase))
            {
                // The engine refused or had nothing to do; do not paint it green.
                severity = InfoBarSeverity.Informational;
            }
            else
            {
                severity = InfoBarSeverity.Success;
            }

            ShowResult(text, severity);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, InfoBarSeverity.Error);
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

    private void HideToTrayButton_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await UpdateStatusAsync();
        if (!_lastError)
        {
            ShowResult("Status refreshed.", InfoBarSeverity.Informational);
        }
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
                ShowResult("Settings saved to settings.psd1 (a .bak copy was kept). The task picks the change up on its next run.", InfoBarSeverity.Success);
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
