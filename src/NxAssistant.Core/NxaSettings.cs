using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace NxAssistant.Core;

/// <summary>
/// 三组件共享配置（%LOCALAPPDATA%\NXAssistant\settings.json）。
/// 解析优先级统一为：环境变量 &gt; settings.json &gt; 内置默认——宿主、插件、托盘必须同一口径，
/// 否则 FILE-001 沙箱根在写入侧与校验侧会分叉。托盘负责读写，宿主/插件每次解析时重读
/// （小文件，热改语义比缓存收益高）。
/// </summary>
public static class NxaSettings
{
    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NXAssistant");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static string? Get(string key)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }
        catch (Exception) { /* 坏配置=无配置，回落 env/默认，不让托盘外组件崩 */ }
        return null;
    }

    public static void Set(string key, string? value)
    {
        var map = Load();
        if (value == null) map.Remove(key);
        else map[key] = value;
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(map,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>环境变量 &gt; settings.json[key] &gt; fallback()。</summary>
    public static string Resolve(string envVar, string key, Func<string> fallback)
    {
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env)) return env!;
        var s = Get(key);
        if (!string.IsNullOrWhiteSpace(s)) return s!;
        return fallback();
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var p in doc.RootElement.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String)
                            map[p.Name] = p.Value.GetString() ?? "";
            }
        }
        catch (Exception) { /* 重写一份坏文件不如从空开始 */ }
        return map;
    }
}
