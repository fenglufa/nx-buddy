# 把已 publish 的插件及其依赖复制进 %UGII_USER_DIR%\startup。
# 只做复制、不删除任何既有 startup DLL（PRD 7.1：不覆盖他人 startup 程序集）。
# 复制后需重启 NX 才会加载；改动正在运行的 NX 前请先保存/关闭工作部件。
[CmdletBinding()]
param(
    [string]$Source = "",
    [string]$UserDir = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Source)) { $Source = Join-Path $root "dist\nx_plugin" }

if ([string]::IsNullOrWhiteSpace($UserDir)) { $UserDir = $env:UGII_USER_DIR }
if ([string]::IsNullOrWhiteSpace($UserDir)) { throw "UGII_USER_DIR not set and no -UserDir given" }

$startup = Join-Path $UserDir "startup"
New-Item -ItemType Directory -Force -Path $startup | Out-Null

if (-not (Test-Path (Join-Path $Source "NxAssistant_NxPlugin.dll"))) {
    throw "NxAssistant_NxPlugin.dll not found; run build/build.ps1 (or dotnet publish) first"
}

Write-Host "Copy $Source -> $startup" -ForegroundColor Cyan
$pending = @()
foreach ($f in Get-ChildItem -Path $Source -Filter *.dll) {
    $name = $f.Name
    try {
        Copy-Item $f.FullName -Destination (Join-Path $startup $name) -Force
        Write-Host ("  + " + $name)
    } catch [System.IO.IOException] {
        # NX 正在运行时会锁定 startup DLL：记为待重启生效，不算失败。
        $pending += $name
        Write-Host ("  ~ " + $name + " (locked; rerun after NX exits)") -ForegroundColor Yellow
    }
}
if ($pending.Count -gt 0) {
    $staged = Join-Path $root "dist\nx_plugin_staged"
    New-Item -ItemType Directory -Force -Path $staged | Out-Null
    Copy-Item -Path (Join-Path $Source "*.dll") -Destination $staged -Force
    Write-Host ("STAGED: " + ($pending -join ', ') + " -> $staged; close NX then rerun this script") -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done. Restart NX 2412 so startup loads NxAssistant_NxPlugin." -ForegroundColor Green
Write-Host "Before restart: save and close the current work part." -ForegroundColor Yellow
