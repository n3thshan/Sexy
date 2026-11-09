using System.Text.RegularExpressions;
using Windows.UI;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
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

        // Mica backdrop; the control card provides contrast on top of it.
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

        AppWindow.Resize(new SizeInt32(570, 460));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var posX = area.WorkArea.X + (area.WorkArea.Width - 570) / 2;
        var posY = area.WorkArea.Y + (area.WorkArea.Height - 460) / 2;
        AppWindow.Move(new PointInt32(posX, posY));

        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        CreateTrayIcon();

        RootGrid.Loaded += async (_, _) => await UpdateStatusAsync();
    }

    // ---------------------------------------------------------------------
    // Tray icon (H.NotifyIcon.WinUI). The Win32 popup menu calls commands,
    // not Click handlers, so every item gets an ICommand. The icon is the
    // Fluent shield; a green badge dot appears while the proxy is ON.
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

    // Plain white Fluent shield, or a white shield on a solid green tile
    // when the proxy is ON. GeneratedIconSource draws synchronously, which
    // the tray icon pipeline requires. Note: use solid brush backgrounds
    // only - a gradient/alpha background crashed the native icon pipeline
    // (0xC000027B in CoreMessagingXP.dll).
    private static GeneratedIconSource TrayIcon(bool proxyOn)
    {
        return new GeneratedIconSource
        {
            Text = "\uEA18", // Shield
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(255, 245, 245, 245)),
            Background = new SolidColorBrush(proxyOn
                ? Color.FromArgb(255, 16, 124, 16)
                : Color.FromArgb(0, 0, 0, 0))
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
        // Tray actions run hidden; the outcome is shown as a balloon tip,
        // not by yanking the window open.
        _ = DoActionAsync(action, fromTray: true);
    }

    private void ShowTrayBalloon(string message, bool isError)
    {
        if (_tray is null) return;

        _tray.ShowNotification(
            "Sexy",
            message,
            isError ? NotificationIcon.Error : NotificationIcon.Info,
            null,       // custom icon handle
            true,       // largeIcon
            true,       // sound
            true,       // respectQuietTime
            false,      // realtime
            null);      // timeout
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

    private void SetScheduleTile(string word, Brush wordBrush, string sub, bool paused)
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

            string Get(string key, string fallback = "-") =>
                map.TryGetValue(key, out var value) ? value : fallback;

            string state = Get("Proxy");
            string stateUpper = state.ToUpperInvariant();
            _isProxyOn = stateUpper.StartsWith("ON", StringComparison.Ordinal);
            UpdateTrayIcon(stateUpper);

            var portText = Get("v2rayN port", string.Empty);
            bool portListening;
            if (string.IsNullOrWhiteSpace(portText))
            {
                // Missing data must never look healthy.
                portListening = false;
                portText = "unknown (not reported by the scheduler)";
            }
            else
            {
                portListening = !portText.TrimStart().StartsWith("NOT listening", StringComparison.OrdinalIgnoreCase);
            }

            var pausedText = Get("Paused", "no");
            _isPaused = pausedText.StartsWith("YES", StringComparison.OrdinalIgnoreCase);

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
                SetScheduleTile("PAUSED", WordAmber, "tap to resume", paused: true);
            }
            else if (insideWindow)
            {
                SetScheduleTile("ON", WordGreen, "tap to pause", paused: false);
            }
            else
            {
                SetScheduleTile("OFF", WordGrey, "outside window - tap to pause", paused: false);
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
            SetScheduleTile("?", WordGrey, "status unavailable", paused: false);

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
    private async Task DoActionAsync(string action, bool fromTray = false)
    {
        if (_busy || _refreshing)
        {
            if (fromTray) ShowTrayBalloon("Another action is still running.", isError: false);
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
            if (fromTray) ShowTrayBalloon(text, severity == InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowResult(ex.Message, InfoBarSeverity.Error);
            if (fromTray) ShowTrayBalloon(ex.Message, isError: true);
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

    private async void HideToTrayButton_Click(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await UpdateStatusAsync();
        if (!_lastError)
        {
            ShowResult("Status refreshed.", InfoBarSeverity.Informational);
        }
    }

    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.TryRead(_engine.EngineDir) ?? new ScheduleSettings();
        var view = new EditScheduleDialog();
        view.LoadFrom(settings);

        var dialog = new ContentDialog
        {
            Title = "Edit Schedule",
            Content = view,
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
        var box = new TextBox
        {
            Text = _engine.ReadLogTail(200),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Width = 560,
            Height = 380
        };

        var dialog = new ContentDialog
        {
            Title = "proxy-schedule.log (last 200 lines)",
            Content = box,
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
