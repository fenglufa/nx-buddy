# 构建 nx-buddy。产出到 dist/，不写入 %UGII_USER_DIR%（避免覆盖已存在的 NX-MCP startup DLL，PRD 7.1）。
# 部署到 NX（复制插件到 startup 并重启 NX）是单独的、需确认的步骤，见 docs/README 部署节。
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$DotNet = "dotnet"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$src  = Join-Path $root "src"
$dist = Join-Path $root "dist"

Write-Host "Building NxAssistant.sln ($Configuration)" -ForegroundColor Cyan
& $DotNet build (Join-Path $src "NxAssistant.sln") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed" }

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# MCP 宿主：自包含发布到 dist/mcp（Agent 的 mcp 配置指向此 exe）
& $DotNet publish (Join-Path $src "NxAssistant.Mcp/NxAssistant.Mcp.csproj") -c $Configuration -o (Join-Path $dist "mcp")
# 托盘（PRD §6 交付形态 NxAssistant.exe）：发布到 dist/tray，--status-json 可无头采集状态
& $DotNet publish (Join-Path $src "NxAssistant.Tray/NxAssistant.Tray.csproj") -c $Configuration -o (Join-Path $dist "tray")
# NX 插件：net48 产物，部署时由安装程序合并进 %UGII_USER_DIR%\startup
& $DotNet publish (Join-Path $src "NxAssistant.NxPlugin/NxAssistant.NxPlugin.csproj") -c $Configuration -o (Join-Path $dist "nx_plugin")

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  MCP host   : $(Join-Path $dist 'mcp\NxAssistant.Mcp.exe')"
Write-Host "  NX plugin  : $(Join-Path $dist 'nx_plugin\NxAssistant_NxPlugin.dll')"
Write-Host "  smoke test : python build/smoke_stdio.py '$(Join-Path $dist 'mcp\NxAssistant.Mcp.exe')'"
