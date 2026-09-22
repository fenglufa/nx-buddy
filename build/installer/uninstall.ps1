# NX 小助手卸载脚本。只拆"自己装的"：
#  - 必须存在 <target>\install.json 且其 target 字段与传入目录一致，否则拒删（防误指他人目录）。
#  - 删托盘自启 Run 值、删安装目录。
#  - %UGII_USER_DIR%\startup 里的插件文件默认不动（避免影响他人引用）；
#    加 -RemovePluginFiles 才按清单里"我们复制过的那些文件名"逐个删，仍绝不触碰清单外文件。
#  - settings.json / license.lic / runs / workspace 属用户数据，一律保留。
[CmdletBinding()]
param(
    [string]$TargetDir = "",
    [string]$UgiiUserDir = "",
    [switch]$RemovePluginFiles
)
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($TargetDir)) {
    $TargetDir = Join-Path $env:LOCALAPPDATA "Programs\NXAssistant"
}
$TargetDir = (New-Object System.IO.DirectoryInfo $TargetDir).FullName
$manifestPath = Join-Path $TargetDir "install.json"
if (-not (Test-Path $manifestPath)) {
    throw "拒绝卸载：$TargetDir 不是 NX 小助手安装目录（缺 install.json）。"
}
$m = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($m.product -ne "NXAssistant" -or $m.target -ne $TargetDir) {
    throw "拒绝卸载：install.json 与该目录不匹配（product=$($m.product), target=$($m.target)）。"
}

Write-Host "卸载 NX 小助手：$TargetDir" -ForegroundColor Cyan

if ($m.run_key_name) {
    $runKeyPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
    if ($null -ne (Get-ItemProperty -Path $runKeyPath -Name $m.run_key_name -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -Path $runKeyPath -Name $m.run_key_name
        Write-Host "  已移除自启值 HKCU Run\$($m.run_key_name)"
    }
}

if ($RemovePluginFiles -and $m.plugin_files) {
    $ud = if ($UgiiUserDir) { $UgiiUserDir } else { $env:UGII_USER_DIR }
    if ([string]::IsNullOrWhiteSpace($ud)) {
        Write-Host "  无 UGII_USER_DIR，未清理 startup 插件文件" -ForegroundColor Yellow
    } else {
        $startup = Join-Path $ud "startup"
        # @() 归一：ConvertTo-Json 单元素数组会变标量，直接 foreach 会逐字符迭代
        foreach ($name in @($m.plugin_files)) {
            $f = Join-Path $startup $name
            if (Test-Path $f) { Remove-Item $f -Force; Write-Host "  已删 $name" }
        }
    }
} elseif ($m.plugin_deployed) {
    Write-Host "  提示：%UGII_USER_DIR%\startup 内的插件文件保留（重启 NX 前加 -RemovePluginFiles 可一并清理）" -ForegroundColor Yellow
}

Remove-Item -Path $TargetDir -Recurse -Force
Write-Host ""
Write-Host "卸载完成。用户数据未动：settings.json / license.lic / runs / workspace 仍在 %LOCALAPPDATA%\NXAssistant。" -ForegroundColor Green
