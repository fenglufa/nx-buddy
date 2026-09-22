using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NxAssistant.Tray;

/// <summary>
/// 最小 MCP stdio 客户端（第十四片）：托盘自己拉起一个 NxAssistant.Mcp.exe 并走
/// initialize → tools/call，与 Agent 共用同一套 33 工具/授权闸门/写前守卫/run 状态机——
/// 审图工作台因此不需要第二套判定实现。逐行 JSON-RPC，语义对齐 build/smoke_stdio.py。
/// 宿主 stderr 只排空不落盘；进程退出时把所有挂起请求置错，UI 层负责按需重启。
/// </summary>
internal sealed class McpHostClient : IDisposable
{
    private readonly string _exe;
    private readonly object _writeGate = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _proc;
    private StreamWriter? _stdin;
    private int _nextId;

    public McpHostClient(string exe) => _exe = exe;

    public bool Alive => _proc is { HasExited: false };

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            if (Alive) return;
            var psi = new ProcessStartInfo
            {
                FileName = _exe,
                WorkingDirectory = Path.GetDirectoryName(_exe) ?? ".",
                UseShellExecute = false,
                // 宿主是 console 子系统 exe；不设这条，GUI 父进程拉起它时 Windows 会
                // 另开一个空白控制台窗口（stdio 已重定向，里面永远是空的）。
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            var proc = Process.Start(psi) ??
                throw new InvalidOperationException("无法启动宿主进程：" + _exe);
            _proc = proc;
            _stdin = proc.StandardInput;
            proc.ErrorDataReceived += (_, _) => { };
            proc.BeginErrorReadLine();
            _ = Task.Run(() => ReadLoop(proc.StandardOutput));
            _ = Task.Run(() => DrainStderr(proc.StandardError));

            await SendRequest("initialize", new Dictionary<string, object?>
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new Dictionary<string, object?>(),
                ["clientInfo"] = new Dictionary<string, object?>
                    { ["name"] = "nxa-tray", ["version"] = "1.0.0" },
            }, ct);
            SendNotification("notifications/initialized");
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>调一个工具并解出 content[].text 里的内层 JSON（宿主 Guard 一律以
    /// {ok:false,error,error_type} 载荷报错，而不是 MCP 协议错误）。</summary>
    public async Task<JsonElement> CallToolAsync(
        string name, IDictionary<string, object?>? args, CancellationToken ct = default)
    {
        await StartAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var resp = await SendRequest("tools/call", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["arguments"] = args ?? new Dictionary<string, object?>(),
        }, timeout.Token);

        var text = new StringBuilder();
        if (resp.ValueKind == JsonValueKind.Object &&
            resp.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
            foreach (var c in content.EnumerateArray())
                if (c.ValueKind == JsonValueKind.Object &&
                    c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    text.Append(t.GetString());
        if (text.Length == 0)
            return ErrorPayload("MCP_EMPTY_RESULT", $"工具 {name} 无文本结果：{resp.GetRawText()}");
        try
        {
            using var doc = JsonDocument.Parse(text.ToString());
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ErrorPayload("MCP_UNPARSED_RESULT", text.ToString());
        }
    }

    private async Task<JsonElement> SendRequest(string method, object? @params, CancellationToken ct)
    {
        var stdin = _stdin ?? throw new InvalidOperationException("宿主未启动");
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params,
        });
        try
        {
            lock (_writeGate)
            {
                stdin.WriteLine(line);
                stdin.Flush();
            }
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            return ErrorPayload("MCP_TIMEOUT", $"宿主 {method} 请求超时/取消");
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return ErrorPayload("MCP_TRANSPORT", ex.Message);
        }
    }

    private void SendNotification(string method)
    {
        var stdin = _stdin;
        if (stdin == null) return;
        var line = JsonSerializer.Serialize(new Dictionary<string, object?>
            { ["jsonrpc"] = "2.0", ["method"] = method });
        try { lock (_writeGate) { stdin.WriteLine(line); stdin.Flush(); } }
        catch (Exception) { /* 宿主已退出：下次调用前由 Alive 判定重启 */ }
    }

    private void ReadLoop(TextReader stdout)
    {
        try
        {
            while (stdout.ReadLine() is string line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch (JsonException) { continue; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idEl) ||
                        !idEl.TryGetInt32(out var id) ||
                        !_pending.TryRemove(id, out var tcs))
                        continue; // 通知/响应外部：忽略
                    if (root.TryGetProperty("error", out var err))
                    {
                        var msg = err.TryGetProperty("message", out var m) &&
                                  m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : err.GetRawText();
                        tcs.TrySetResult(ErrorPayload("MCP_RPC_ERROR", msg));
                    }
                    else
                    {
                        var result = root.TryGetProperty("result", out var r)
                            ? r.Clone() : JsonDocument.Parse("{}").RootElement;
                        tcs.TrySetResult(result);
                    }
                }
            }
        }
        catch (Exception) { /* 进程退出/管道断开 */ }
        FaultAll("宿主进程已退出");
    }

    private static void DrainStderr(TextReader stderr)
    {
        try { while (stderr.ReadLine() != null) { } } catch { }
    }

    private void FaultAll(string reason)
    {
        foreach (var kv in _pending.ToArray())
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetResult(ErrorPayload("MCP_HOST_GONE", reason));
    }

    internal static JsonElement ErrorPayload(string code, string message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            { ["ok"] = false, ["error"] = message, ["error_type"] = code });
        return JsonDocument.Parse(bytes).RootElement.Clone();
    }

    public void Dispose()
    {
        try
        {
            lock (_writeGate) { _stdin?.Close(); }
            if (_proc is { HasExited: false })
            {
                if (!_proc.WaitForExit(3000))
                    _proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception) { }
        _proc?.Dispose();
        _proc = null;
        _stdin = null;
    }
}
