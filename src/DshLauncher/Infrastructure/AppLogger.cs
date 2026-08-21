using System.Text;

namespace DshLauncher.Infrastructure;

internal sealed class AppLogger : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public AppLogger()
    {
        AppPaths.EnsureCreated();
        _writer = new StreamWriter(
            new FileStream(AppPaths.LogFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message}{Environment.NewLine}{error}");

    private void Write(string level, string message)
    {
        lock (_sync)
        {
            _writer.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }
}
