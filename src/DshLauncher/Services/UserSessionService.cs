using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DshLauncher.Infrastructure;

namespace DshLauncher.Services;

internal static class UserSessionService
{
    public const string RelaunchMarker = "--unelevated-relaunch";

    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity)
            .IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool WasRelaunched(string[] args) =>
        args.Contains(RelaunchMarker, StringComparer.OrdinalIgnoreCase);

    public static bool TryRelaunchUnelevated(string[] args, AppLogger log)
    {
        object? shell = null;
        try
        {
            var executable = Environment.ProcessPath;
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (string.IsNullOrWhiteSpace(executable) || shellType is null)
            {
                return false;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return false;
            }

            var forwarded = args
                .Where(value => !value.Equals(
                    RelaunchMarker,
                    StringComparison.OrdinalIgnoreCase))
                .Append(RelaunchMarker)
                .Select(QuoteArgument);
            var arguments = string.Join(" ", forwarded);
            shellType.InvokeMember(
                "ShellExecute",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args:
                [
                    executable,
                    arguments,
                    Environment.CurrentDirectory,
                    "open",
                    1
                ]);
            log.Info("Requested an unelevated Launcher restart through Windows Shell.");
            return true;
        }
        catch (Exception error)
        {
            log.Error("Could not restart Launcher as the interactive user.", error);
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static string QuoteArgument(string value) =>
        Convert.ToChar(34) + value + Convert.ToChar(34);
}
