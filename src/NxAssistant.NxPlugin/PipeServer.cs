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
/// THREADING: 骨架阶段在请求线程内直接调用 NXOpen。NXOpen 对主线程有亲和性，
/// 接线批次（部署+重启 NX 联调时）必须改为 marshal 到 NX 主线程执行（见 ToolService 注释）。
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
            try
            {
                using var server = new NamedPipeServerStream(
                    NxIpc.FullPipeName(),
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.None);
                server.WaitForConnection();
                HandleConnection(server);
            }
            catch (Exception ex)
            {
                NxLog.Write("pipe accept error: " + ex.Message);
                Thread.Sleep(200);
            }
        }
    }

    private void HandleConnection(NamedPipeServerStream server)
    {
        using var reader = new StreamReader(server, new UTF8Encoding(false));
        using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = false };
        string? line;
        while (_running && (line = reader.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            string responseLine = ProcessLine(line);
            writer.Write(responseLine + "\n");
            writer.Flush();
        }
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
