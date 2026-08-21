using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using DshLauncher.Infrastructure;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal sealed class DshRuntimeService : IAsyncDisposable
{
    private readonly AppLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private Process? _process;
    private WindowsJob? _job;

    public DshRuntimeService(AppLogger log)
    {
        _log = log;
    }

    public bool OwnsRunningProcess => _process is { HasExited: false };
    public event EventHandler? ProcessStateChanged;

    public async Task StartAsync(
        DshInstallation installation,
        string dshHome,
        string workingDirectory,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (OwnsRunningProcess)
        {
            throw new InvalidOperationException(
                "DSH is already running under Launcher control. Use Restart to apply a different configuration.");
        }
        if (await IsDshReadyAsync(port, cancellationToken))
        {
            _log.Info($"Attached to an existing DSH service on port {port}.");
            ProcessStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (PortService.IsListening(port))
        {
            throw new InvalidOperationException($"Port {port} is already used by another program.");
        }

        var node = installation.NodeExecutable;
        if (!File.Exists(node))
        {
            throw new FileNotFoundException("node.exe for the selected DSH installation was not found.", node);
        }

        var entry = NpmService.GetEntryScript(installation);
        if (!File.Exists(entry))
        {
            throw new FileNotFoundException("The selected DSH CLI entry was not found.", entry);
        }

        var info = new ProcessStartInfo
        {
            FileName = node,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        info.ArgumentList.Add(entry);
        info.ArgumentList.Add("web");
        info.ArgumentList.Add("--host");
        info.ArgumentList.Add("127.0.0.1");
        info.ArgumentList.Add("--port");
        info.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--no-open");
        info.Environment["DSH_HOME"] = Path.GetFullPath(dshHome);

        var startupErrors = new List<string>();
        var startupErrorsLock = new object();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) _log.Info("DSH: " + e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            _log.Warn("DSH: " + e.Data);
            lock (startupErrorsLock)
            {
                if (startupErrors.Count >= 40) startupErrors.RemoveAt(0);
                startupErrors.Add(e.Data);
            }
        };
        process.Exited += OnProcessExited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start DSH.");
        }

        _process = process;
        _job = new WindowsJob();
        _job.AddProcess(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _log.Info($"Started DSH {installation.InstalledVersion}; PID={process.Id}; port={port}.");
        ProcessStateChanged?.Invoke(this, EventArgs.Empty);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                process.WaitForExit();
                string? diagnostic;
                lock (startupErrorsLock)
                {
                    diagnostic = startupErrors.LastOrDefault(line =>
                        line.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                        ?? startupErrors.LastOrDefault();
                }

                var detail = string.IsNullOrWhiteSpace(diagnostic)
                    ? string.Empty
                    : Environment.NewLine + Environment.NewLine + diagnostic;
                throw new InvalidOperationException(
                    $"DSH exited during startup with code {process.ExitCode}.{detail}");
            }

            if (await IsDshReadyAsync(port, cancellationToken))
            {
                return;
            }

            await Task.Delay(500, cancellationToken);
        }

        await StopAsync(cancellationToken);
        throw new TimeoutException("DSH Web UI did not become ready within 60 seconds.");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        _log.Info("Stopping the Launcher-owned DSH process tree.");
        try
        {
            process.Kill(true);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            CleanupProcess();
            ProcessStateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task RestartAsync(
        DshInstallation installation,
        string dshHome,
        string workingDirectory,
        int port,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(installation, dshHome, workingDirectory, port, cancellationToken);
    }

    public async Task<bool> IsDshReadyAsync(int port, CancellationToken cancellationToken = default)
    {
        try
        {
            var rpcId = Guid.NewGuid().ToString();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{port}/api/session.list")
            {
                Content = JsonContent.Create(new
                {
                    type = "client-request",
                    rpcId,
                    method = "session.list",
                    payload = new { }
                })
            };
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("result", out var result) &&
                   result.TryGetProperty("ok", out var ok) &&
                   ok.ValueKind == JsonValueKind.True &&
                   result.TryGetProperty("value", out var value) &&
                   value.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    public static void OpenWeb(int port)
    {
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{port}")
        {
            UseShellExecute = true
        });
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (sender is Process process)
        {
            _log.Info($"DSH process exited with code {process.ExitCode}.");
        }

        ProcessStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupProcess()
    {
        if (_process is not null)
        {
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }

        _job?.Dispose();
        _job = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CleanupProcess();
        _http.Dispose();
    }
}
