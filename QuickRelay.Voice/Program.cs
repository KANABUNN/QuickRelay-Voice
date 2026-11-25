using System.Runtime.InteropServices;

namespace QuickRelay.Voice;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\QuickRelay.Voice", out var acquired);
        using var showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\QuickRelay.Voice.Show");
        if (!acquired)
        {
            // The running copy may sit in the tray; let it take the foreground and ask it to show its window.
            AllowSetForegroundWindow(AnyProcess);
            showRequest.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var controller = new VoiceController();
        using var form = new MainForm(controller);
        var wait = ThreadPool.RegisterWaitForSingleObject(showRequest, (_, _) => form.RequestShow(), null,
            Timeout.Infinite, executeOnlyOnce: false);
        try
        {
            Application.Run(form);
        }
        finally
        {
            wait.Unregister(null);
        }
    }

    private const int AnyProcess = -1;

    [DllImport("user32", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
