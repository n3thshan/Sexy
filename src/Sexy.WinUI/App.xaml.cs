using Microsoft.UI.Xaml;

namespace Sexy;

public partial class App : Application
{
    private Window? m_window;
    private static Mutex? s_mutex;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Single instance: a second launch exits quietly; the running window
        // stays in charge instead of competing over the tray icon.
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
        m_window.Activate();
    }
}
