using System.Text;
using System.Text.Json;
using NxAssistant.Core;
using NxAssistant.Core.Protocol;

namespace NxAssistant.Mcp;

/// <summary>
/// 宿主→插件的命名管道客户端。发送一行 {id,method,params[,token]}，读回一行响应信封。
/// 与 NX-MCP 的"每次请求拉起一个 client 进程"不同，这里走常驻管道；连接失败清晰可报，供 Agent 判读。
/// </summary>
public sealed class NxPluginClient
{
    public int TimeoutMs { get; set; } = 120_000;

    public async Task<BridgeResponse> SendAsync(string method, object? @params, CancellationToken ct = default)
    {
        var token = Environment.GetEnvironmentVariable(NxIpc.TokenEnvVar);
        var req = new Dictionary<string, object?>
        {
            ["id"] = Guid.NewGuid().ToString("N"),
            ["method"] = method,
            ["params"] = @params ?? new Dictionary<string, object?>(),
        };
        if (!string.IsNullOrEmpty(token)) req["token"] = token;
        var line = JsonSerializer.Serialize(req, NxJson.Options);

        using var pipe = new System.IO.Pipes.NamedPipeClientStream(
            ".", NxIpc.FullPipeName(), System.IO.Pipes.PipeDirection.InOut);
        try
        {
            await pipe.ConnectAsync(TimeoutMs, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new NxNotConnectedException(
                "NX 小助手插件未连接：请确认 NX 2412 已启动并加载了 NxAssistant.NxPlugin.dll", ex);
        }

        // leaveOpen=true：reader/writer 释放时不得抢先关掉管道，否则 writer.Dispose
        // 会对已关闭管道二次 Close 而抛 ObjectDisposedException，吞掉成功响应。
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = false };
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        await writer.WriteAsync(line + "\n").ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);

        var respLine = await reader.ReadLineAsync().ConfigureAwait(false);
        if (respLine == null)
            throw new NxNotConnectedException("插件未返回响应（管道被关闭）");
        var resp = JsonSerializer.Deserialize<BridgeResponse>(respLine, NxJson.Options)
            ?? throw new InvalidOperationException("插件返回了空响应");
        return resp;
    }

    /// <summary>取 result；插件报错(含未实现)时抛，交给上层工具转成可读结果。</summary>
    public async Task<JsonElement> CallAsync(string method, object? @params, CancellationToken ct = default)
    {
        var resp = await SendAsync(method, @params, ct).ConfigureAwait(false);
        if (!resp.Ok)
        {
            var msg = resp.Error?.Message ?? "NX 操作失败";
            var type = resp.Error?.Type;
            throw new NxPluginErrorException(string.IsNullOrEmpty(type) ? msg : $"{type}: {msg}");
        }
        return resp.Result ?? default;
    }
}

public sealed class NxNotConnectedException : Exception
{
    public NxNotConnectedException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed class NxPluginErrorException : Exception
{
    public NxPluginErrorException(string message) : base(message) { }
}
