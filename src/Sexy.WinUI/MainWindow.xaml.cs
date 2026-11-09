using System.Text.RegularExpressions;
using H.NotifyIcon;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Sexy;

public sealed partial class MainWindow : Window
{
    private static readonly Color Green = ColorHelper.FromArgb(255, 15, 123, 15);
    private static readonly Color Red = ColorHelper.FromArgb(255, 196, 43, 28);
    private static readonly Color Grey = ColorHelper.FromArgb(255, 134, 134, 134);
    private static readonly Color TrayBlue = ColorHelper.FromArgb(255, 0, 120, 215);

    private readonly EngineService _engine = new();
    private readonly Brush _white = new SolidColorBrush(ColorHelper.FromArgb(255, 255, 255, 255));

    private bool _busy;
    private bool _refreshing;
    private bool _exiting;
    private bool _canTurnOn = true;
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
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        AppWindow.Resize(new SizeInt32(570, 640));

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var posX = area.WorkArea.X + (area.WorkArea.Width - 570) / 2;
        var posY = area.WorkArea.Y + (area.WorkArea.Height - 640) / 2;
        AppWindow.Move(new PointInt32(posX, posY));

        AppWindow.Closing += OnAppWindowClosing;
        Activated += OnWindowActivated;

        CreateTrayIcon();

        RootGrid.Loaded += async (_, _) => await UpdateStatusAsync();
    }

    // ---------------------------------------------------------------------
    // Tray icon (H.NotifyIcon.WinUI). The Win32 popup menu calls commands,
    // not Click handlers, so every item gets an ICommand.
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

        var tray = new TaskbarIcon
        {
            ToolTipText = "Sexy - v2rayN schedule",
            NoLeftClickDelay = true
        };
        tray.IconSource = new GeneratedIconSource
        {
            Text = "S",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 56,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = _white,
            Background = new SolidColorBrush(TrayBlue)
        };
        tray.ContextFlyout = menu;
        tray.LeftClickCommand = new RelayCommand(ToggleWindowFromTray);

        RootGrid.Children.Add(tray);
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
        // A state-changing tray action must never run blind: bring the
        // window up first so the user sees the result banner.
        ShowFromTray();
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
        OnButton.IsEnabled = enabled;
        OffButton.IsEnabled = enabled;
        ResumeButton.IsEnabled = enabled;
        PauseButton.IsEnabled = enabled;
        EditButton.IsEnabled = enabled;
        LogButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;

        if (enabled && !_canTurnOn)
        {
            OnButton.IsEnabled = false;
        }
    }

    private static Brush Solid(Color color) => new SolidColorBrush(color);

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

            StateHeadline.Text = stateUpper.StartsWith("ON", StringComparison.Ordinal)
                ? "Proxy is ON"
                : stateUpper.StartsWith("OTHER", StringComparison.Ordinal)
                    ? "Another proxy is configured"
                    : "Proxy is OFF";
            StateDot.Fill = stateUpper.StartsWith("ON", StringComparison.Ordinal)
                ? Solid(Green)
                : stateUpper.StartsWith("OTHER", StringComparison.Ordinal)
                    ? Solid(Red)
                    : Solid(Grey);

            ValueProxy.Text = state;
            ValueProxy.Foreground = stateUpper.StartsWith("ON", StringComparison.Ordinal)
                ? Solid(Green)
                : stateUpper.StartsWith("OTHER", StringComparison.Ordinal)
                    ? Solid(Red)
                    : null; // keep the theme default

            var server = Get("Server");
            ValueServer.Text = server;
            ToolTipService.SetToolTip(ValueServer, server);

            var portText = Get("v2rayN port", string.Empty);
            bool portListening;
            if (string.IsNullOrWhiteSpace(portText))
            {
                // Missing data must never look healthy.
                portListening = false;
                portText = "unknown (not reported by the scheduler)";
                ValuePort.Foreground = Solid(Red);
            }
            else
            {
                portListening = !portText.TrimStart().StartsWith("NOT listening", StringComparison.OrdinalIgnoreCase);
                ValuePort.Foreground = portListening ? Solid(Green) : Solid(Red);
            }
            ValuePort.Text = portText;
            ToolTipService.SetToolTip(ValuePort, portText);

            ValueSchedule.Text = settings is not null
                ? $"ON {settings.OnStart}-{settings.OnEnd}, {EngineService.FormatDays(settings.Days)}"
                : Get("Schedule");

            ValueNow.Text = Get("Schedule state now");

            var pausedText = Get("Paused", "no");
            var isPaused = pausedText.StartsWith("YES", StringComparison.OrdinalIgnoreCase);
            if (isPaused)
            {
                var since = _engine.GetPausedSince();
                pausedText = since.HasValue
                    ? $"yes, since {since.Value:yyyy-MM-dd HH:mm} ({EngineService.FormatAgo(since.Value)})"
                    : "yes";
                ValuePaused.Foreground = Solid(Red);
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

            // Decide whether "Turn Proxy ON" can work at all.
            _canTurnOn = true;
            var hint = string.Empty;
            if (stateUpper.StartsWith("OTHER", StringComparison.Ordinal))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable while another proxy is configured. Clear it first.";
            }
            else if (string.IsNullOrWhiteSpace(Get("v2rayN port", string.Empty)))
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: the scheduler did not report the v2rayN port.";
            }
            else if (!portListening)
            {
                _canTurnOn = false;
                hint = "Turn ON is unavailable: v2rayN is not listening, so the proxy would break your connection.";
            }

            HintText.Text = hint;
            HintText.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            _lastRefresh = DateTime.Now;
        }
        catch (Exception ex)
        {
            _lastError = true;
            _canTurnOn = false;

            StateHeadline.Text = "Scheduler unreachable";
            StateDot.Fill = Solid(Grey);

            foreach (var block in new[]
                     {
                         ValueProxy, ValueServer, ValuePort, ValueSchedule,
                         ValueNow, ValuePaused, ValueNext, ValueLastRun
                     })
            {
                block.Text = "-";
            }

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
                ShowResult(text, InfoBarSeverity.Error);
            }
            else if (Regex.IsMatch(text, "not changed|already|Nothing to do", RegexOptions.IgnoreCase))
            {
                // The engine refused or had nothing to do; do not paint it green.
                ShowResult(text, InfoBarSeverity.Informational);
            }
            else
            {
                ShowResult(text, InfoBarSeverity.Success);
            }
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

    private async void OnButton_Click(object sender, RoutedEventArgs e) => await DoActionAsync("On");
    private async void OffButton_Click(object sender, RoutedEventArgs e) => await DoActionAsync("Off");
    private async void ResumeButton_Click(object sender, RoutedEventArgs e) => await DoActionAsync("Resume");
    private async void PauseButton_Click(object sender, RoutedEventArgs e) => await DoActionAsync("Pause");

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await UpdateStatusAsync();
        if (!_lastError)
        {
            ShowResult("Status refreshed.", InfoBarSeverity.Informational);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => ExitApp();

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
