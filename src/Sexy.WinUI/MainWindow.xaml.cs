using Windows.UI;
using Windows.Graphics;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Sexy;

/// <summary>
/// Invisible host for the tray icon. There is no main window: left-clicking
/// the tray shield opens the panel (a H.NotifyIcon TrayPopup), which is the
/// entire UI of the app.
/// </summary>
public sealed partial class MainWindow : Window
{
    private TaskbarIcon? _tray;
    private TrayPanel? _panel;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();

        Title = "Sexy";
        AppWindow.Title = "Sexy";
        SystemBackdrop = new MicaBackdrop();

        // Borderless 1x1 host; effectively invisible even if it flashes.
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }
        AppWindow.Resize(new SizeInt32(1, 1));

        AppWindow.Closing += (s, args) =>
        {
            if (_exiting) return;
            // Never show a window by accident; X is not reachable anyway.
            args.Cancel = true;
            AppWindow.Hide();
        };

        CreateTrayIcon();

        RootGrid.Loaded += (_, _) =>
        {
            // Force one status refresh so the tray glyph and tooltip are right
            // after startup; the panel refreshes itself on every open too.
            _ = _panel?.RefreshAsync();
        };
    }

    private void CreateTrayIcon()
    {
        _panel = new TrayPanel();
        _panel.StateChanged += stateUpper => UpdateTrayIcon(stateUpper);
        _panel.ExitRequested += ExitApp;

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Turn Proxy ON",
            Command = new RelayCommand(() => _ = _panel.TrayActionAsync("On"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Turn Proxy OFF",
            Command = new RelayCommand(() => _ = _panel.TrayActionAsync("Off"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Pause Schedule",
            Command = new RelayCommand(() => _ = _panel.TrayActionAsync("Pause"))
        });
        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "Resume Schedule",
            Command = new RelayCommand(() => _ = _panel.TrayActionAsync("Resume"))
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
            NoLeftClickDelay = true,
            PopupActivation = PopupActivationMode.LeftClick,
            TrayPopup = _panel
        };
        _tray.IconSource = TrayIcon(proxyOn: false);
        _tray.ContextFlyout = menu;

        RootGrid.Children.Add(_tray);
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
        _tray.ToolTipText = stateUpper.StartsWith("ON", StringComparison.Ordinal)
            ? "Sexy - Proxy ON"
            : stateUpper.StartsWith("OTHER", StringComparison.Ordinal)
                ? "Sexy - Another proxy is configured"
                : "Sexy - Proxy OFF";
    }

    public void HideToTray() => AppWindow.Hide();

    private void ExitApp()
    {
        _exiting = true;
        Application.Current.Exit();
    }
}
