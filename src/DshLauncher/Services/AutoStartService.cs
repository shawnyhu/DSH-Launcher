using Microsoft.Win32;

namespace DshLauncher.Services;

internal static class AutoStartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DSHLauncher";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string value &&
               !string.IsNullOrWhiteSpace(value);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled)
        {
            key.SetValue(ValueName, "\"" + Environment.ProcessPath + "\" --autostart");
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }
}
