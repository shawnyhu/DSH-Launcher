namespace DshLauncher.Infrastructure;

internal static class AppPaths
{
    public static readonly string DataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSHLauncher");

    public static readonly string SettingsFile = Path.Combine(DataRoot, "settings.json");
    public static readonly string LogDirectory = Path.Combine(DataRoot, "logs");
    public static readonly string LogFile = Path.Combine(LogDirectory, "launcher.log");
    public static readonly string ManagedInstallRoot = Path.Combine(DataRoot, "runtimes");

    public static string DefaultDshHome => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".dsh");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(ManagedInstallRoot);
    }
}
