using Microsoft.Win32;

namespace QuickRelay.Voice;

internal enum StartupState { Off, On, Elsewhere }

/// <summary>
/// Registers QRV in the per-user Run key so Windows starts it at sign-in. No administrator rights are needed, and
/// the entry appears on Task Manager's Startup tab, where the user can also disable it.
/// </summary>
internal sealed class StartupRegistration(string runKey, string approvedKey, string? executable)
{
    internal const string ValueName = "QuickRelay Voice";
    private const string UserRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UserApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>The executable that would be registered, or null when QRV was not started from its .exe.</summary>
    internal string? Executable { get; } = executable;

    internal bool CanRegister => Executable is not null;

    internal static StartupRegistration ForCurrentUser()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, typeof(StartupRegistration).Assembly.GetName().Name + ".exe");
        return new StartupRegistration(UserRunKey, UserApprovedKey, File.Exists(candidate) ? Path.GetFullPath(candidate) : null);
    }

    /// <summary>The command currently registered for QRV, whichever copy registered it.</summary>
    internal string? RegisteredCommand
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKey);
            return key?.GetValue(ValueName) as string;
        }
    }

    internal StartupState State => RegisteredCommand switch
    {
        null => StartupState.Off,
        var command when Executable is not null
            && string.Equals(CommandPath(command), Executable, StringComparison.OrdinalIgnoreCase) => StartupState.On,
        _ => StartupState.Elsewhere
    };

    /// <summary>True when the entry was switched off on Task Manager's Startup tab; Windows then skips it.</summary>
    internal bool DisabledInTaskManager
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(approvedKey);
            return key?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) != 0;
        }
    }

    internal void Enable()
    {
        if (Executable is null)
            throw new InvalidOperationException("実行ファイルの場所が分からないため、スタートアップに登録できません。");
        using var key = Registry.CurrentUser.CreateSubKey(runKey);
        key.SetValue(ValueName, $"\"{Executable}\"", RegistryValueKind.String);
    }

    internal void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string CommandPath(string command)
    {
        var text = command.Trim();
        if (!text.StartsWith('"'))
            return text;
        var end = text.IndexOf('"', 1);
        return end < 0 ? text[1..] : text[1..end];
    }
}
