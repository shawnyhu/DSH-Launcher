namespace DshLauncher.Models;

public enum DshInstallScope
{
    Global,
    Managed
}

public sealed class DshInstallation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "DSH";
    public DshInstallScope Scope { get; set; }
    public string InstallRoot { get; set; } = string.Empty;
    public string PackageRoot { get; set; } = string.Empty;
    public string NodeExecutable { get; set; } = string.Empty;
    public string NpmExecutable { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public DateTimeOffset? LastVerifiedAt { get; set; }

    public override string ToString()
    {
        var scope = Scope == DshInstallScope.Global ? "\u5168\u5C40" : "\u72EC\u7ACB";
        return string.IsNullOrWhiteSpace(InstalledVersion)
            ? Name + "\uFF08" + scope + "\uFF0C\u672A\u9A8C\u8BC1\uFF09"
            : Name + " " + InstalledVersion + "\uFF08" + scope + "\uFF09";
    }
}

public sealed class DshHomeEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "\u9ED8\u8BA4\u6570\u636E";
    public string Path { get; set; } = string.Empty;
    public string? LastObservedWriterVersion { get; set; }
    public DateTimeOffset? LastObservedWriteAt { get; set; }
    public bool ObservationReliable { get; set; }

    public override string ToString() => $"{Name} \u00B7 {Path}";
}

public sealed class LauncherSettings
{
    public int SchemaVersion { get; set; } = 1;
    public List<DshInstallation> Installations { get; set; } = [];
    public List<DshHomeEntry> Homes { get; set; } = [];
    public Guid? SelectedInstallationId { get; set; }
    public Guid? SelectedHomeId { get; set; }
    public int Port { get; set; } = 3080;
    public string WorkingDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool StartDshWithLauncher { get; set; } = true;
    public bool OpenBrowserAfterStart { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool NotifyOnCompletion { get; set; } = true;
    public string LauncherUpdateRepository { get; set; } = "shawnyhu/DSH-Launcher";

    public DshInstallation? SelectedInstallation =>
        Installations.FirstOrDefault(x => x.Id == SelectedInstallationId);

    public DshHomeEntry? SelectedHome =>
        Homes.FirstOrDefault(x => x.Id == SelectedHomeId);
}

public enum DshActivityState
{
    Stopped,
    Idle,
    Busy,
    Attention,
    Incompatible
}

public sealed record DshStatusSnapshot(
    DshActivityState State,
    int RunningSessions,
    int PendingQuestions,
    int PendingApprovals,
    string Summary)
{
    public static readonly DshStatusSnapshot Stopped = new(
        DshActivityState.Stopped, 0, 0, 0, "DSH \u672A\u8FD0\u884C");
}

public sealed record DshNotification(
    string Title,
    string Message,
    ToolTipIcon Icon,
    bool IsCompletion = false);
