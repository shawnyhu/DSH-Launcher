using System.Diagnostics;
using System.Text;

namespace DshLauncher.Services;

internal sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;
    public string CombinedOutput => string.Join(
        Environment.NewLine,
        new[] { StandardOutput.Trim(), StandardError.Trim() }.Where(x => x.Length > 0));
}

internal sealed class CommandRunner
{
    public async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var argumentList = arguments.ToArray();
        var info = CreateStartInfo(executable, argumentList);
        info.WorkingDirectory = Directory.Exists(workingDirectory)
            ? workingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                if (pair.Value is null)
                {
                    info.Environment.Remove(pair.Key);
                }
                else
                {
                    info.Environment[pair.Key] = pair.Value;
                }
            }
        }

        using var process = new Process { StartInfo = info };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动命令：{executable}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = timeout.HasValue
            ? new CancellationTokenSource(timeout.Value)
            : new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            if (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"命令执行超时：{Path.GetFileName(executable)}");
            }

            throw;
        }

        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static string? FindOnPath(string fileName)
    {
        var candidates = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.Combine(path, fileName));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(executable);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var info = BaseStartInfo(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe");
            info.Arguments = "/d /s /c \"" + BuildCmdCommand(executable, arguments) + "\"";
            return info;
        }

        var direct = BaseStartInfo(executable);
        foreach (var argument in arguments)
        {
            direct.ArgumentList.Add(argument);
        }

        return direct;
    }

    private static ProcessStartInfo BaseStartInfo(string fileName) => new()
    {
        FileName = fileName,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8
    };

    private static string BuildCmdCommand(string executable, IReadOnlyList<string> arguments)
    {
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        return string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    }
}
