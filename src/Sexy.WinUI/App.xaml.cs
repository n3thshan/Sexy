using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Sexy;

public partial class App : Application
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr PerMonitorV2 = new(-4);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    private MainWindow? m_window;
    private static Mutex? s_mutex;

    public App()
    {
        // This must happen before the first window is created, otherwise
        // Windows bitmap-stretches the whole app and it looks blurry.
        // (A manifest can't declare this here because the MSIX tooling
        // generates and owns the embedded manifest.)
        SetProcessDpiAwarenessContext(PerMonitorV2);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second launch exits quietly. The app lives in
        // the system tray; the shield is the whole interface.
        s_mutex = new Mutex(false, @"Local\SexyWinUI");
        bool have;
        try
        {
            have = s_mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // A previous instance was killed; the mutex is ours now.
            have = true;
        }

        if (!have)
        {
            Environment.Exit(0);
            return;
        }

        m_window = new MainWindow();
        // Activate once so the window's visual tree loads and the tray icon
        // is created, then hide it: the app lives in the tray from here on.
        m_window.Activate();
        m_window.HideToTray();
    }
}
