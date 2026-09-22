namespace NxAssistant.Core;

/// <summary>本机 IPC 约定：命名管道名与环境变量（PRD §6：仅 127.0.0.1 / 命名管道，带令牌）。</summary>
public static class NxIpc
{
    /// <summary>命名管道基名；宿主与插件必须一致。多实例可用后缀区分。</summary>
    public const string PipeName = "nxassistant-mcp";

    public static string FullPipeName(string? instance = null) =>
        string.IsNullOrEmpty(instance) ? PipeName : $"{PipeName}-{instance}";

    /// <summary>共享令牌环境变量：非空时 IPC 报文须携带匹配 token，挡本机其它进程误连。</summary>
    public const string TokenEnvVar = "NXA_IPC_TOKEN";
}
