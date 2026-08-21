using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DshLauncher.Infrastructure;
using DshLauncher.Models;

namespace DshLauncher.Services;

internal sealed class DshEventMonitor : IAsyncDisposable
{
    private readonly AppLogger _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _sync = new();
    private readonly HashSet<string> _runningSessions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _runningJobs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingQuestions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly HashSet<string> _notifiedAttention = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _titles = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _loop;
    private System.Threading.Timer? _completionTimer;
    private DshNotification? _pendingCompletion;
    private int _port;

    public DshEventMonitor(AppLogger log)
    {
        _log = log;
    }

    public event EventHandler<DshStatusSnapshot>? StatusChanged;
    public event EventHandler<DshNotification>? NotificationRequested;
    public event EventHandler<Uri>? SocketConnected;

    public void Start(int port)
    {
        if (_lifetime is not null && _port == port)
        {
            return;
        }

        Stop();
        _port = port;
        _lifetime = new CancellationTokenSource();
        _loop = MonitorLoopAsync(port, _lifetime.Token);
    }

    public void Stop()
    {
        if (_lifetime is null)
        {
            return;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = null;
        _loop = null;
        ClearTransientState();
        PublishStatus(false);
    }

    private async Task MonitorLoopAsync(int port, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await IsReadyAsync(port, cancellationToken))
                {
                    ClearTransientState();
                    PublishStatus(false);
                    await Task.Delay(delay, cancellationToken);
                    delay = TimeSpan.FromSeconds(Math.Min(15, delay.TotalSeconds * 1.6));
                    continue;
                }

                delay = TimeSpan.FromSeconds(2);
                ClearTransientState();
                await LoadSessionBaselineAsync(port, cancellationToken);
                PublishStatus(true);

                using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var host = ReceiveSocketAsync(
                    new Uri($"ws://127.0.0.1:{port}/api/events.host"),
                    connection.Token);
                var mux = ReceiveSocketAsync(
                    new Uri($"ws://127.0.0.1:{port}/api/events.mux"),
                    connection.Token);
                await Task.WhenAny(host, mux);
                connection.Cancel();

                try
                {
                    await Task.WhenAll(host, mux);
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                _log.Warn("DSH event connection failed: " + error.Message);
            }

            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReceiveSocketAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{uri.Port}");
        await socket.ConnectAsync(uri, cancellationToken);
        _log.Info("Connected to " + uri.AbsolutePath);
        SocketConnected?.Invoke(this, uri);

        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var receive = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                idle.Token);
            using var frame = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, receive.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    frame.Write(buffer, 0, result.Count);
                }
            }
            while (!result.EndOfMessage);

            if (frame.Length > 0)
            {
                ProcessFrame(Encoding.UTF8.GetString(frame.GetBuffer(), 0, checked((int)frame.Length)));
            }
        }
    }

    private void ProcessFrame(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodNode) ||
                !root.TryGetProperty("payload", out var payload))
            {
                return;
            }

            var method = methodNode.GetString();
            var sessionId = GetString(payload, "sessionId") ?? "unknown";
            lock (_sync)
            {
                switch (method)
                {
                    case "host/session-status":
                        SetMembership(_runningSessions, sessionId, GetBoolean(payload, "running"));
                        break;
                    case "question/requested":
                    {
                        var key = "question:" + sessionId;
                        _pendingQuestions.Add(key);
                        NotifyQuestion(key, payload);
                        break;
                    }
                    case "question/resolved":
                        RemoveSessionKeys(_pendingQuestions, "question:" + sessionId);
                        _notifiedAttention.Remove("question:" + sessionId);
                        break;
                    case "approval/requested":
                    {
                        var approvalId = GetString(payload, "approvalId") ?? sessionId;
                        var key = "approval:" + approvalId;
                        _pendingApprovals.Add(key);
                        NotifyApproval(key, payload);
                        break;
                    }
                    case "approval/resolved":
                    {
                        var approvalId = GetString(payload, "approvalId") ?? sessionId;
                        _pendingApprovals.Remove("approval:" + approvalId);
                        _notifiedAttention.Remove("approval:" + approvalId);
                        break;
                    }
                    case "session/event":
                        ProcessSessionEvent(sessionId, payload);
                        break;
                    case "session/jobs":
                        ProcessJobs(sessionId, payload);
                        break;
                    case "session/projection":
                        if (GetString(payload, "key") == "title")
                        {
                            var title = GetString(payload, "value");
                            if (!string.IsNullOrWhiteSpace(title)) _titles[sessionId] = title;
                        }
                        break;
                }
            }

            PublishStatus(true);
        }
        catch (JsonException error)
        {
            _log.Warn("Ignored an invalid DSH event frame: " + error.Message);
        }
    }

    private void ProcessSessionEvent(string sessionId, JsonElement payload)
    {
        if (!payload.TryGetProperty("event", out var eventNode))
        {
            return;
        }

        var eventType = GetString(eventNode, "type");
        if (eventType == "turn/start")
        {
            _runningSessions.Add(sessionId);
            return;
        }

        if (eventType != "turn/end")
        {
            return;
        }

        _runningSessions.Remove(sessionId);
        var title = _titles.TryGetValue(sessionId, out var known)
            ? known
            : "DSH conversation";
        var kind = eventNode.TryGetProperty("data", out var data) &&
                   data.TryGetProperty("reason", out var reason)
            ? GetString(reason, "kind")
            : null;
        var message = kind switch
        {
            "completed" => title + " completed.",
            "failed" => title + " failed.",
            "cancelled" => title + " was cancelled.",
            _ => title + " ended."
        };
        ScheduleCompletion(new DshNotification(
            kind == "completed" ? "Conversation completed" : "Conversation ended",
            message,
            kind == "completed" ? ToolTipIcon.Info : ToolTipIcon.Warning,
            true));
    }

    private void ProcessJobs(string sessionId, JsonElement payload)
    {
        _runningJobs.RemoveWhere(key => key.StartsWith(sessionId + ":", StringComparison.Ordinal));
        if (!payload.TryGetProperty("jobs", out var jobs) ||
            jobs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var job in jobs.EnumerateArray())
        {
            var status = GetString(job, "status");
            if (status is "running" or "stopping")
            {
                var id = GetString(job, "id") ?? Guid.NewGuid().ToString("N");
                _runningJobs.Add(sessionId + ":" + id);
            }
        }
    }

    private void NotifyQuestion(string key, JsonElement payload)
    {
        if (!_notifiedAttention.Add(key))
        {
            return;
        }

        var message = "Open DSH to answer the pending question.";
        if (payload.TryGetProperty("questions", out var questions) &&
            questions.ValueKind == JsonValueKind.Array &&
            questions.GetArrayLength() > 0)
        {
            var first = questions[0];
            message = GetString(first, "question") ?? message;
            var header = GetString(first, "header");
            if (!string.IsNullOrWhiteSpace(header)) message = header + ": " + message;
        }

        NotificationRequested?.Invoke(this, new DshNotification(
            "DSH needs your answer",
            Limit(message, 220),
            ToolTipIcon.Warning));
    }

    private void NotifyApproval(string key, JsonElement payload)
    {
        if (!_notifiedAttention.Add(key))
        {
            return;
        }

        var tool = GetString(payload, "toolName") ?? "tool";
        var reason = GetString(payload, "reason");
        var message = string.IsNullOrWhiteSpace(reason)
            ? tool
            : tool + ": " + reason;
        NotificationRequested?.Invoke(this, new DshNotification(
            "DSH requests permission",
            Limit(message, 220),
            ToolTipIcon.Warning));
    }

    private void ScheduleCompletion(DshNotification notification)
    {
        _pendingCompletion = notification;
        _completionTimer?.Dispose();
        _completionTimer = new System.Threading.Timer(_ =>
        {
            DshNotification? pending;
            lock (_sync)
            {
                pending = _pendingCompletion;
                _pendingCompletion = null;
            }

            if (pending is not null)
            {
                NotificationRequested?.Invoke(this, pending);
            }
        }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
    }

    private async Task LoadSessionBaselineAsync(int port, CancellationToken cancellationToken)
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
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = document.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("ok", out var ok) ||
            !ok.GetBoolean() ||
            !result.TryGetProperty("value", out var value) ||
            !value.TryGetProperty("items", out var items))
        {
            return;
        }

        lock (_sync)
        {
            _runningSessions.Clear();
            foreach (var item in items.EnumerateArray())
            {
                var id = GetString(item, "sessionId");
                if (id is null)
                {
                    continue;
                }

                if (GetBoolean(item, "running")) _runningSessions.Add(id);
                if (item.TryGetProperty("projections", out var projections) &&
                    projections.TryGetProperty("values", out var values) &&
                    values.TryGetProperty("title", out var title) &&
                    title.ValueKind == JsonValueKind.String)
                {
                    _titles[id] = title.GetString()!;
                }
            }
        }
    }

    private async Task<bool> IsReadyAsync(int port, CancellationToken cancellationToken)
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
                   ok.ValueKind == JsonValueKind.True;
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

    private void PublishStatus(bool serviceReady)
    {
        DshStatusSnapshot snapshot;
        lock (_sync)
        {
            if (!serviceReady)
            {
                snapshot = DshStatusSnapshot.Stopped;
            }
            else
            {
                var attention = _pendingQuestions.Count + _pendingApprovals.Count;
                var running = _runningSessions.Count + _runningJobs.Count;
                var state = attention > 0
                    ? DshActivityState.Attention
                    : running > 0
                        ? DshActivityState.Busy
                        : DshActivityState.Idle;
                var summary = attention > 0
                    ? $"DSH needs attention: {_pendingQuestions.Count} question(s), {_pendingApprovals.Count} approval(s)"
                    : running > 0
                        ? $"DSH is busy: {running} active session/job(s)"
                        : "DSH is running and idle";
                snapshot = new DshStatusSnapshot(
                    state,
                    running,
                    _pendingQuestions.Count,
                    _pendingApprovals.Count,
                    summary);
            }
        }

        StatusChanged?.Invoke(this, snapshot);
    }

    private void ClearTransientState()
    {
        lock (_sync)
        {
            _runningSessions.Clear();
            _runningJobs.Clear();
            _pendingQuestions.Clear();
            _pendingApprovals.Clear();
            _titles.Clear();
        }
    }

    private static void SetMembership(HashSet<string> set, string key, bool value)
    {
        if (value) set.Add(key);
        else set.Remove(key);
    }

    private static void RemoveSessionKeys(HashSet<string> set, string prefix) =>
        set.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool GetBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string Limit(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "\u2026";

    public async ValueTask DisposeAsync()
    {
        var loop = _loop;
        Stop();
        if (loop is not null)
        {
            try { await loop; } catch (OperationCanceledException) { }
        }

        _completionTimer?.Dispose();
        _http.Dispose();
    }
}
