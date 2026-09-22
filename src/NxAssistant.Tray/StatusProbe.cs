using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using NxAssistant.Core;
using NxAssistant.Licensing;

namespace NxAssistant.Tray;

/// <summary>托盘状态快照（PRD §7.1：未授权/已授权/NX 未连接/MCP 忙碌 四观）。</summary>
public sealed class TrayStatus
{
    public bool Licensed { get; set; }
    public string LicenseState { get; set; } = "";
    public string LicenseReason { get; set; } = "";
    public int? DaysLeft { get; set; }
    public string LicensePath { get; set; } = "";
    public string LicId { get; set; } = "";
    public string Customer { get; set; } = "";
    public string NotAfter { get; set; } = "";
    public string MachineHash { get; set; } = "";
    public bool PluginOnline { get; set; }
    public bool McpRunning { get; set; }
    public bool ReviewBusy { get; set; }
    public string WorkspaceRoot { get; set; } = "";
    public string RulesDir { get; set; } = "";
    public bool RulesPackFound { get; set; }
    public string RulesStatePath { get; set; } = "";
    public int RulesDisabledCount { get; set; }
    public int RulesOverridesCount { get; set; }
    public string RulesStateLatest { get; set; } = "";
    public string SettingsPath { get; set; } = "";
    public string PluginLogPath { get; set; } = "";
    public string RunsRoot { get; set; } = "";
    public string McpExe { get; set; } = "";
}

/// <summary>
/// 状态探针：不经 IPC、不抢管道请求句柄——插件在线=命名管道名仍占据管道命名空间
/// （宿主是逐请求短连接，托盘 ping 会和宿主抢单 worker 队列，存在性探测无侵入）。
/// MCP 忙碌近似为宿主进程存在 + runs 目录有 running 态 run。
/// </summary>
public static class StatusProbe
{
    public static string ResolveMcpExe()
    {
        var s = NxaSettings.Get("mcp_exe");
        if (!string.IsNullOrWhiteSpace(s)) return s;
        var sibling = Path.Combine(AppContext.BaseDirectory, "..", "mcp", "NxAssistant.Mcp.exe");
        return File.Exists(sibling) ? Path.GetFullPath(sibling) : "NxAssistant.Mcp.exe";
    }

    public static string ResolveLocalAppDataDir() => NxaSettings.Dir;

    public static TrayStatus Read()
    {
        var licPath = NxaSettings.Resolve("NXA_LICENSE_PATH", "license_path",
            () => LicenseManager.DefaultLicensePath);
        var lic = LicenseManager.Check(licPath);
        var mcpExe = ResolveMcpExe();
        var (rulesDir, rulesFound) = ResolveRulesPack(mcpExe);
        var localApp = NxaSettings.Dir;
        var rulesStatePath = NxaSettings.Resolve("NXA_RULES_STATE", "rules_state_path",
            () => NxAssistant.Rules.RulesState.DefaultPath());
        var rulesState = NxAssistant.Rules.RulesState.Load(rulesStatePath);

        return new TrayStatus
        {
            Licensed = lic.IsValid,
            LicenseState = LicenseManager.StateToCode(lic.State),
            LicenseReason = lic.Message,
            DaysLeft = lic.DaysLeft,
            LicensePath = lic.LicensePath,
            LicId = lic.Payload?.LicId ?? "",
            Customer = lic.Payload?.Customer ?? "",
            NotAfter = lic.Payload?.NotAfter ?? "",
            MachineHash = lic.MachineHash,
            PluginOnline = PipeExists(NxIpc.PipeName),
            McpRunning = Process.GetProcessesByName("NxAssistant.Mcp").Length > 0,
            ReviewBusy = AnyRunInProgress(Path.Combine(localApp, "runs")),
            WorkspaceRoot = NxaSettings.Resolve("NXA_WORKSPACE", "workspace",
                () => Path.Combine(localApp, "workspace")),
            RulesDir = rulesDir,
            RulesPackFound = rulesFound,
            RulesStatePath = rulesStatePath,
            RulesDisabledCount = rulesState.Disabled.Count,
            RulesOverridesCount = rulesState.Data.Count,
            RulesStateLatest = rulesState.Log.Count > 0
                ? $"{rulesState.Log[^1].T} {rulesState.Log[^1].Action} {rulesState.Log[^1].Detail}"
                : "",
            SettingsPath = NxaSettings.FilePath,
            PluginLogPath = Path.Combine(Path.GetTempPath(), "nxa_plugin.log"),
            RunsRoot = Path.Combine(localApp, "runs"),
            McpExe = mcpExe,
        };
    }

    public static string ToJson(TrayStatus s) =>
        JsonSerializer.Serialize(s, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });

    /// <summary>可复制的 MCP 配置片段（PRD §7.1）：command 指向宿主 exe 绝对路径。</summary>
    public static string McpConfigSnippet(TrayStatus s)
    {
        var doc = new Dictionary<string, object?>
        {
            ["mcpServers"] = new Dictionary<string, object?>
            {
                ["nx-buddy"] = new Dictionary<string, object?>
                {
                    ["command"] = s.McpExe,
                },
            },
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool PipeExists(string pipeName)
    {
        try
        {
            return Directory.EnumerateFiles(@"\\.\pipe\")
                .Any(n => string.Equals(n, @"\\.\pipe\" + pipeName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private static bool AnyRunInProgress(string runsRoot)
    {
        try
        {
            if (!Directory.Exists(runsRoot)) return false;
            foreach (var dir in Directory.EnumerateDirectories(runsRoot))
            {
                var run = Path.Combine(dir, "run.json");
                if (!File.Exists(run)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(run));
                if (doc.RootElement.TryGetProperty("status", out var st) &&
                    st.GetString() == "running") return true;
            }
        }
        catch (Exception) { /* 状态探针永不因读盘失败而崩 */ }
        return false;
    }

    /// <summary>与宿主 HostRules 同序：env/settings → 宿主同目录 company_v3 → 逐级父目录 docs/company_v3。</summary>
    private static (string Dir, bool Found) ResolveRulesPack(string mcpExe)
    {
        var fromSetting = NxaSettings.Resolve("NXA_RULES_DIR", "rules_dir", () => "");
        if (!string.IsNullOrWhiteSpace(fromSetting))
            return (fromSetting, File.Exists(Path.Combine(fromSetting, "pack.json")));
        var baseDir = Path.GetDirectoryName(mcpExe);
        if (string.IsNullOrEmpty(baseDir)) baseDir = AppContext.BaseDirectory;
        var local = Path.Combine(baseDir, "company_v3");
        if (File.Exists(Path.Combine(local, "pack.json"))) return (local, true);
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            var cand = Path.Combine(dir.FullName, "docs", "company_v3");
            if (File.Exists(Path.Combine(cand, "pack.json"))) return (cand, true);
            dir = dir.Parent;
        }
        return (local, false);
    }
}
