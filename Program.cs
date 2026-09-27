using FanDeck.UI;

namespace FanDeck;

static class Program
{
    const string MutexName = @"Local\FanDeck.SingleInstance";
    public const string ShowEventName = @"Local\FanDeck.Show";

    /// <summary>Set by the tray app: hands the fans back to the BIOS if the process is going down.</summary>
    public static Action? EmergencyRelease;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--detect", StringComparison.OrdinalIgnoreCase))
            return Detect.Run(args.Length > 1 ? args[1] : null);

        using var mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance)
        {
            // Already running: ask that copy to show its window. This process was just started by the user,
            // so it may hand over the right to take the foreground.
            try
            {
                using var show = EventWaitHandle.OpenExisting(ShowEventName);
                AllowSetForegroundWindow(-1);
                show.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
            }
            return 0;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Write(e.ExceptionObject.ToString() ?? "unknown crash");
            EmergencyRelease?.Invoke();
        };

        ApplicationConfiguration.Initialize();
        bool startInTray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
        using var app = new TrayApp(startInTray);
        Application.Run(app);
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
