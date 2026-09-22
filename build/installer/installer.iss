; NX 小助手 Inno Setup 壳（第十二片续）。
; 原则：iss 只负责"双击体验 + 卸载登记"，业务逻辑不重实现——
;   三组件与规则包由 [Files] 落位（规则包直取仓库 docs，随宿主同目录，
;   这是独立安装态下宿主规则解析链唯一可靠落点）；
;   自启由 [Registry] 写 HKCU Run（Inno 卸载自动撤销）；
;   startup 插件部署/清理走 deploy_plugin.ps1 已冒烟的合并语义：
;   安装时勾任务才执行、卸载时只删"与 {app}\nx_plugin 同名且存在"的文件，绝不触碰清单外文件。
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
; 代码签名就绪后启用（需 OV/EV 证书 + signtool 在 PATH）：
; signtool=... / 参考 make_installer.ps1 的签名段

[Files]
Source: "..\..\dist\mcp\*"; DestDir: "{app}\mcp"; Flags: recursesubdirs createallsubdirs; Excludes: "company_v3\*"
Source: "..\..\docs\company_v3\*"; DestDir: "{app}\mcp\company_v3"; Flags: recursesubdirs createallsubdirs
Source: "..\..\dist\tray\*"; DestDir: "{app}\tray"; Flags: recursesubdirs createallsubdirs
Source: "..\..\dist\nx_plugin\*"; DestDir: "{app}\nx_plugin"; Flags: recursesubdirs createallsubdirs
Source: "install.ps1"; DestDir: "{app}\installer"
Source: "uninstall.ps1"; DestDir: "{app}\installer"
Source: "..\deploy_plugin.ps1"; DestDir: "{app}\installer"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "NXAssistant"; ValueData: """{app}\tray\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue

[Tasks]
Name: "deployplugin"; Description: "把 NX 插件合并部署进 %UGII_USER_DIR%\startup（只复制、不删他人文件；需重启 NX 生效）"; Check: UgiiDirExists

[Run]
Filename: "{app}\tray\{#MyAppExeName}"; Description: "启动 NX 小助手托盘"; Flags: postinstall nowait skipifsilent

[Code]
function UgiiDirExists: Boolean;
begin
  Result := GetEnv('UGII_USER_DIR') <> '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and WizardIsTaskSelected('deployplugin') then
    Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\installer\deploy_plugin.ps1') +
      '" -Source "' + ExpandConstant('{app}\nx_plugin') + '"',
      '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
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
