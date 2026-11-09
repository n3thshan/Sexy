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

    private MainWindow? _window;
    // Kept as a static field so the mutex stays held for the process lifetime.
    private static Mutex? s_mutex;
    private static EventWaitHandle? s_showEvent;

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
        // Single instance: a second launch signals the running instance to
        // raise its window, then exits quietly.
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
            // Already running (e.g. hidden in the tray after a logon start).
            // Ask the running instance to bring its window up, then exit.
            try
            {
                if (EventWaitHandle.OpenExisting(@"Local\SexyShowWindow") is { } show)
                {
                    show.Set();
                    show.Dispose();
                }
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // The running instance is mid-shutdown; nothing to signal.
            }

            Environment.Exit(0);
            return;
        }

        // Create the window first, then listen for "show yourself" from later launches.
        _window = new MainWindow();
        var queue = _window.DispatcherQueue;
        s_showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SexyShowWindow");
        var showThread = new Thread(() =>
        {
            while (true)
            {
                s_showEvent.WaitOne();
                queue?.TryEnqueue(() => _window?.ShowFromTray());
            }
        })
        {
            IsBackground = true
        };
        showThread.Start();

        bool startInTray = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        if (startInTray)
        {
            // Launched by the "Start with Windows" entry: no window, just the
            // tray. Activate first so Loaded fires (H.NotifyIcon creates the
            // tray icon in Loaded), then hide immediately - one frame flash.
            _window.Activate();
            _window.HideToTray();
        }
        else
        {
            _window.Activate();
        }
    }
}
