# 用 Inno Setup 编译 installer.iss -> dist\installer\NXAssistant-Setup-<版本>.exe
# 依赖：先跑 build.ps1 产出 dist/mcp、dist/tray、dist/nx_plugin；本机装好 ISCC.exe
[CmdletBinding()]
param(
    [string]$ISCC = "",
    [string]$Pfx = "",          # 代码签名证书（可选；留空则不签）
    [string]$PfxPass = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$iss  = Join-Path $root "build\installer\installer.iss"

if ([string]::IsNullOrWhiteSpace($ISCC)) {
    $pf86 = ${env:ProgramFiles(x86)}
    $cands = @(
        (Join-Path $pf86 "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")   # winget 用户级安装落点
    )
    foreach ($c in $cands) { if (Test-Path $c) { $ISCC = $c; break } }
    if (-not $ISCC) { $g = Get-Command iscc -ErrorAction SilentlyContinue; if ($g) { $ISCC = $g.Source } }
    if (-not $ISCC) { throw "找不到 ISCC.exe。装 Inno Setup 6（winget install JRSoftware.InnoSetup）或用 -ISCC 指定路径。" }
}

foreach ($d in @("mcp","tray","nx_plugin")) {
    $p = Join-Path $root "dist\$d"
    if (-not (Test-Path $p)) { throw "缺 $p —— 先跑 build/build.ps1" }
}
if (-not (Test-Path (Join-Path $root "docs\company_v3\pack.json"))) { throw "缺规则包 docs\company_v3" }

$args = @($iss)
if ($Pfx) { $args += "/SignTool=signtool" }   # 需在 Inno 里配置或 signtool 在 PATH
Write-Host "Compiling $iss with $ISCC" -ForegroundColor Cyan
& $ISCC @args
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败 (exit $LASTEXITCODE)" }

$ver = (Select-String -Path $iss -Pattern '^#define MyAppVersion "([^"]+)"').Matches[0].Groups[1].Value
$out = Join-Path $root "dist\installer\NXAssistant-Setup-$ver.exe"
if (-not (Test-Path $out)) { throw "编译成功但未找到产物：$out" }
Write-Host "`nInstaller: $out" -ForegroundColor Green
Write-Host ("Size: {0:N0} bytes" -f (Get-Item $out).Length)
