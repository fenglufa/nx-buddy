; NX 小助手 Inno Setup 壳（第十二片续）。
; 原则：iss 只负责"双击体验 + 卸载登记"，业务逻辑不重实现——
;   三组件与规则包由 [Files] 落位（规则包直取仓库 docs，随宿主同目录，
;   这是独立安装态下宿主规则解析链唯一可靠落点）；
;   自启由 [Registry] 写 HKCU Run（Inno 卸载自动撤销）；
;   startup 插件部署/清理走 deploy_plugin.ps1 已冒烟的合并语义：
;   安装尾声无条件尝试部署（不给用户选——用 NX 小助手这步必须发生），
;   缺 UGII_USER_DIR/脚本报错在完成时弹提示并给补救命令；
;   卸载时只删"与 {app}\nx_plugin 同名且存在"的文件，绝不触碰清单外文件。
; 编译：build/make_installer.ps1（自动定位 ISCC；产物 dist\installer\NXAssistant-Setup-*.exe）

#define MyAppName "NX 小助手"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "产品方（待填公司名）"
#define MyAppExeName "NxAssistant.exe"

[Setup]
; 固定 GUID：升级安装认这台机器上的自己，不产生重复卸载项
AppId={{7E3C9A25-5B41-4F8E-9D2A-6C1F8B4E0A57}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\NXAssistant
DisableProgramGroupPage=yes
; 与脚本安装器同口径：用户级、免 UAC（一 Key 一机的单机产品形态）
PrivilegesRequired=lowest
OutputDir=..\..\dist\installer
OutputBaseFilename=NXAssistant-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
UninstallDisplayIcon={app}\tray\{#MyAppExeName}
WizardStyle=modern
; 简体中文界面（语言包随仓库走，来源 jrsoftware/issrc 官方 unofficial 翻译，已补 UTF-8 BOM）。
; 只挂这一种语言 = 全程中文、无语言选择对话框；Restart Manager 等系统弹窗文案也随语言包变中文。
; LanguageName 影响 /LANG 参数与注册表 Display 名；产品面向国内用户，默认即中文。
; 代码签名就绪后启用（需 OV/EV 证书 + signtool 在 PATH）：
; signtool=... / 参考 make_installer.ps1 的签名段

[Languages]
Name: "chinesesimplified"; MessagesFile: "Languages\ChineseSimplified.isl"

[Files]
; ignoreversion 必需：.NET 产物都带 1.0.0.0 文件版本，Inno 默认对"同版本号"的已存在文件
; 直接跳过不覆盖——实测升级安装一个 DLL 都没换（日志 Same version. Skipping.），
; 只有卸载重装才更新。升级必须无条件覆盖自家产物。
Source: "..\..\dist\mcp\*"; DestDir: "{app}\mcp"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "company_v3\*"
Source: "..\..\docs\company_v3\*"; DestDir: "{app}\mcp\company_v3"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\dist\tray\*"; DestDir: "{app}\tray"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\dist\nx_plugin\*"; DestDir: "{app}\nx_plugin"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "uninstall.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "..\deploy_plugin.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "NXAssistant"; ValueData: """{app}\tray\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式（双击即开主页）"

[Icons]
Name: "{autodesktop}\NX 小助手"; Filename: "{app}\tray\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\tray\{#MyAppExeName}"; Description: "启动 NX 小助手托盘"; Flags: postinstall nowait skipifsilent

[Code]
var
  DeployMsg: String;
  DeployFailed: Boolean;

// 升级安装时托盘/宿主多半在跑：托盘开机自启；MCP 客户端（Qoder/Claude 等）会持续拉起
// NxAssistant.Mcp.exe 常驻——文件被锁就会弹 "文件正在使用" 对话框。这两个都是自家进程，
// 强杀无用户数据损失（托盘装完即重启，宿主由客户端下次连接重拉），故在安装前主动关闭。
// 先托盘后宿主：托盘退出会带走自己的子宿主，但运行中可能按需重拉，顺序反了会留竞态。
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec('taskkill.exe', '/F /IM NxAssistant.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/F /IM NxAssistant.Mcp.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);   // 给文件句柄释放留一点余量
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Ugii, ManualCmd: String;
begin
  if CurStep = ssPostInstall then
  begin
    Ugii := GetEnv('UGII_USER_DIR');
    ManualCmd := 'powershell -NoProfile -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\installer\deploy_plugin.ps1') + '" -Source "' +
      ExpandConstant('{app}\nx_plugin') + '"';
    if Ugii = '' then
    begin
      // 没找到 NX 用户定制目录：托盘/宿主照常可用，但 NX 不会加载插件——必须让用户知道原因。
      DeployFailed := True;
      DeployMsg := '未检测到环境变量 UGII_USER_DIR（NX 用户定制目录），NX 插件本次未部署。' + #13#10 +
                   '托盘与 MCP 宿主已安装可用，但在 NX 环境就绪前，NX 内不会有本插件。' + #13#10 + #13#10 +
                   '确认 UGII_USER_DIR 已定义后，运行以下命令补部署（之后重启 NX 生效）：' + #13#10 + ManualCmd;
    end
    else if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\installer\deploy_plugin.ps1') +
      '" -Source "' + ExpandConstant('{app}\nx_plugin') + '"',
      '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      DeployFailed := True;
      DeployMsg := 'NX 插件部署脚本执行失败（退出码 ' + IntToStr(ResultCode) + '），NX 内暂不会有本插件。' + #13#10 +
                   '托盘与 MCP 宿主已安装可用。可参照上方脚本输出排查，或稍后运行：' + #13#10 + ManualCmd;
    end
    else
      DeployMsg := '';  // 成功不提示（用户裁决）：脚本输出已可见，末尾窗体即完成页
  end
  else if CurStep = ssDone then
  begin
    // 静默/IT 推送不打扰；向导模式只在部署失败（含缺 UGII_USER_DIR）时弹错误框，成功不再弹窗。
    if not WizardSilent and DeployFailed then
      MsgBox(DeployMsg, mbError, MB_OK);
  end;
end;

// 卸载清 startup：只删我们在 {app}\nx_plugin 里带过、且在 startup 同名的文件；
// 用户数据（settings.json / license.lic / runs / workspace）一律不动。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Ugii, Startup, PluginDir, Names: string;
  FindRec: TFindRec;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Ugii := GetEnv('UGII_USER_DIR');
  if Ugii = '' then Exit;
  Startup := Ugii + '\startup';
  PluginDir := ExpandConstant('{app}\nx_plugin');
  if (not DirExists(Startup)) or (not DirExists(PluginDir)) then Exit;
  if FindFirst(PluginDir + '\*.dll', FindRec) then
  begin
    try
      repeat
        Names := Names + ';' + FindRec.Name;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if FindFirst(Startup + '\*.dll', FindRec) then
  begin
    try
      repeat
        if Pos(';' + FindRec.Name + ';', ';' + Names + ';') > 0 then
          DeleteFile(Startup + '\' + FindRec.Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;
