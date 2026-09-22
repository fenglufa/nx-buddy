# NX 小助手安装脚本（V1 安装包骨架，PRD §6/§7.1）。
# 三组件装进用户目录（免 UAC）：宿主 <target>\mcp、托盘 <target>\tray、插件源 <target>\nx_plugin。
# 规则包随宿主复制到 <target>\mcp\company_v3——独立安装态下这是宿主规则解析
# （NXA_RULES_DIR > settings.json > 同目录 > docs 祖先）唯一可靠落点；缺了会 fail-closed 拒掉所有受守卫写。
# 插件进 %UGII_USER_DIR%\startup 复用 deploy_plugin.ps1 的合并语义：只复制、绝不删他人 startup 文件。
# 卸载：install.ps1/uninstall.ps1/deploy_plugin.ps1 一并复制进 <target>\installer，无仓库也能卸。
[CmdletBinding()]
param(
    [string]$SourceRoot = "",
    [string]$TargetDir = "",
    [string]$UgiiUserDir = "",
    [switch]$SkipPluginDeploy,
    [switch]$SkipRunKey
)
$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $installerDir)   # build\installer -> 仓库根
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = Join-Path $root "dist" }
if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $env:LOCALAPPDATA "Programs\NXAssistant"
}
$TargetDir = (New-Object System.IO.DirectoryInfo $TargetDir).FullName

$hostExe = Join-Path $SourceRoot "mcp\NxAssistant.Mcp.exe"
$trayExe = Join-Path $SourceRoot "tray\NxAssistant.exe"
$pluginSrc = Join-Path $SourceRoot "nx_plugin"
$rulesSrc = Join-Path $root "docs\company_v3"
foreach ($p in @($hostExe, $trayExe, (Join-Path $pluginSrc "NxAssistant_NxPlugin.dll"))) {
    if (-not (Test-Path $p)) { throw "缺产物：$p —— 先运行 build/build.ps1" }
}
if (-not (Test-Path (Join-Path $rulesSrc "pack.json"))) { throw "缺规则包：$rulesSrc\pack.json" }

Write-Host "安装 NX 小助手 -> $TargetDir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null

function Copy-Tree($src, $dst) {
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item -Path (Join-Path $src "*") -Destination $dst -Recurse -Force
}
Copy-Tree (Split-Path -Parent $hostExe) (Join-Path $TargetDir "mcp")
Copy-Tree (Split-Path -Parent $trayExe) (Join-Path $TargetDir "tray")
Copy-Tree $pluginSrc (Join-Path $TargetDir "nx_plugin")
# 脚本自身随装，供卸载
New-Item -ItemType Directory -Force -Path (Join-Path $TargetDir "installer") | Out-Null
Copy-Item (Join-Path $installerDir "*.ps1") -Destination (Join-Path $TargetDir "installer") -Force
Copy-Item (Join-Path $root "build\deploy_plugin.ps1") -Destination (Join-Path $TargetDir "installer") -Force

# 规则包随宿主
Copy-Tree $rulesSrc (Join-Path $TargetDir "mcp\company_v3")

# 托盘开机自启（HKCU Run，不需要管理员）
$runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runKeyName = "NXAssistant"
$installedTray = Join-Path $TargetDir "tray\NxAssistant.exe"
if (-not $SkipRunKey) {
    Set-ItemProperty -Path $runKeyPath -Name $runKeyName -Value "`"$installedTray`""
    Write-Host "  自启已登记：HKCU Run\$runKeyName"
}

# 插件部署进 %UGII_USER_DIR%\startup（合并语义；NX 开着时走暂存）
$pluginDeployed = $false
$pluginFiles = @()
# 调用随装到 installer\ 的副本，脱离仓库也能部署
$deployScript = Join-Path $TargetDir "installer\deploy_plugin.ps1"
# -UgiiUserDir 覆盖 env（端到端冒烟指向临时假目录）；否则用真实 %UGII_USER_DIR%
$targetUserDir = if ($UgiiUserDir) { $UgiiUserDir } else { $env:UGII_USER_DIR }
if (-not $SkipPluginDeploy) {
    if ([string]::IsNullOrWhiteSpace($targetUserDir)) {
        Write-Host "  未设置 UGII_USER_DIR：跳过 startup 插件部署（可稍后跑 installer\deploy_plugin.ps1）" -ForegroundColor Yellow
    } else {
        & $deployScript -Source (Join-Path $TargetDir "nx_plugin") -UserDir $targetUserDir
        $pluginDeployed = $true
        $pluginFiles = @(Get-ChildItem $pluginSrc -Filter *.dll | ForEach-Object { $_.Name })
    }
} else {
    Write-Host "  已按参数跳过 startup 插件部署" -ForegroundColor Yellow
}

# 安装清单（卸载器的删除许可证）
$version = (Get-Item $hostExe).VersionInfo.FileVersion
$manifest = [ordered]@{
    product       = "NXAssistant"
    installed_at  = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ss")
    version       = $version
    target        = $TargetDir
    run_key_name  = $(if ($SkipRunKey) { "" } else { $runKeyName })
    plugin_deployed = $pluginDeployed
    plugin_files  = $pluginFiles
    rules_pack    = "mcp\company_v3"
}
$manifest | ConvertTo-Json | Set-Content -Path (Join-Path $TargetDir "install.json") -Encoding UTF8

Write-Host ""
Write-Host "安装完成。" -ForegroundColor Green
Write-Host "  托盘：$installedTray（可在托盘右键复制 MCP 配置片段）"
Write-Host "  宿主：$(Join-Path $TargetDir 'mcp\NxAssistant.Mcp.exe')"
Write-Host "  Agent 的 mcp 配置片段（command 为绝对路径）：" -ForegroundColor Cyan
Write-Host "    { `"mcpServers`": { `"nx-buddy`": { `"command`": `"$($TargetDir)\mcp\NxAssistant.Mcp.exe`" } } }"
if (-not $pluginDeployed) {
    Write-Host "  提示：插件未部署/待重启生效时，跑 $TargetDir\installer\deploy_plugin.ps1" -ForegroundColor Yellow
}
