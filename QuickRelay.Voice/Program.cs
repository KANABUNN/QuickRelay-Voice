namespace QuickRelay.Voice;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instance = new Mutex(true, @"Local\QuickRelay.Voice", out var acquired);
        if (!acquired)
        {
            MessageBox.Show("QuickRelay Voice はすでに起動しています。", "QuickRelay Voice");
            return;
        }

        ApplicationConfiguration.Initialize();
        using var controller = new VoiceController();
        Application.Run(new MainForm(controller));
    }
}
