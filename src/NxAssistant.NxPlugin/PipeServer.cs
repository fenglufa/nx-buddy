using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using NxAssistant.Core;
using NxAssistant.Core.Protocol;

namespace NxAssistant.NxPlugin;

/// <summary>
/// 命名管道服务端：常驻 NX 进程内，接收宿主转发的一行一请求（{id,method,params}），
/// 交给 ToolService 分发，回写一行响应信封。仅本机命名管道（PRD §6 IPC 约束）。
///
/// 线程模型：worker 线程只做收发；NXOpen 调用由 ToolService.Dispatch 内部
/// 经 MainThread.Run marshal 回 NX 主线程执行。
/// </summary>
internal sealed class PipeServer
{
    public static readonly PipeServer Instance = new PipeServer();

    private Thread? _worker;
    private volatile bool _running;

    public void EnsureStarted()
    {
        if (_running) return;
        _running = true;
        _worker = new Thread(Run) { IsBackground = true, Name = "NXA-PipeServer" };
        _worker.Start();
    }

    public void Stop() => _running = false;

    private void Run()
    {
        while (_running)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    NxIpc.FullPipeName(),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();
                HandleConnection(server);
            }
            catch (IOException)
            {
                // 宿主进程退出时挂起的 WaitForConnection 会抛"管道已中断"：属客户端离开的常态，不记错误。
                Thread.Sleep(50); // 防持续中断态下空转
            }
            catch (Exception ex)
            {
                NxLog.Write("pipe accept error: " + ex.Message);
                Thread.Sleep(200);
            }
            finally
            {
                // 客户端先断开时 Disconnect 会抛"管道已中断"——属常态，不得冒成 accept error。
                try { server?.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        // leaveOpen=true：reader/writer 释放时不得关掉管道句柄（管道生命周期归 Run 的 using 管），
        // 否则 writer.Dispose 二次 Close 会抛 ObjectDisposedException 冒泡成伪"accept error"。
        using var reader = new StreamReader(server, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = false };
        string? line;
        try
        {
            while (_running && (line = reader.ReadLine()) != null)
            {
                if (line.Trim().Length == 0) continue;
                string responseLine = ProcessLine(line);
                writer.Write(responseLine + "\n");
                writer.Flush();
            }
        }
        catch (IOException) { /* 客户端读完即断开属常态，不算错误 */ }
    }

    private string ProcessLine(string line)
    {
        string id = string.Empty;
        try
        {
            var req = JsonSerializer.Deserialize<BridgeRequest>(line, NxJson.Options);
            if (req == null || string.IsNullOrEmpty(req.Method))
                throw new ArgumentException("request must contain a non-empty method");
            id = req.Id ?? string.Empty;
            EnsureAuthorized(line);
            var result = ToolService.Dispatch(req.Method, req.Params);
            return JsonSerializer.Serialize(BridgeResponse.Success(id, result, NxJson.Options), NxJson.Options);
        }
        catch (Exception ex)
        {
            NxLog.Write($"dispatch error: {ex}");
            return JsonSerializer.Serialize(
                BridgeResponse.Failure(id, ex.Message, ex.GetType().FullName ?? ex.GetType().Name, ex.StackTrace),
                NxJson.Options);
        }
    }

    private static void EnsureAuthorized(string rawLine)
    {
        var token = Environment.GetEnvironmentVariable(NxIpc.TokenEnvVar);
        if (string.IsNullOrEmpty(token)) return; // 未设令牌则本机默认放行
        using var doc = JsonDocument.Parse(rawLine);
        var provided = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        if (provided != token)
            throw new UnauthorizedAccessException("IPC token mismatch or missing");
    }
}
