using DshLauncher.Infrastructure;
using DshLauncher.Models;
using DshLauncher.Services;

namespace DshLauncher.Diagnostics;

internal static class EventProbe
{
    public static async Task<int> RunAsync(int port)
    {
        AppPaths.EnsureCreated();
        using var log = new AppLogger();
        await using var monitor = new DshEventMonitor(log);
        var ready = new TaskCompletionSource<DshStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        DshStatusSnapshot? current = null;
        var sockets = new HashSet<string>(StringComparer.Ordinal);

        void TryComplete()
        {
            lock (gate)
            {
                if (current is not null && sockets.Count >= 2)
                {
                    ready.TrySetResult(current);
                }
            }
        }

        monitor.StatusChanged += (_, status) =>
        {
            if (status.State != DshActivityState.Stopped)
            {
                lock (gate) current = status;
                TryComplete();
            }
        };
        monitor.SocketConnected += (_, uri) =>
        {
            lock (gate) sockets.Add(uri.AbsolutePath);
            TryComplete();
        };
        monitor.Start(port);
        try
        {
            var status = await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
            log.Info("Event probe passed: " + status.Summary);
            return 0;
        }
        catch (TimeoutException)
        {
            log.Error($"Event probe timed out on port {port}.");
            return 1;
        }
    }
}
