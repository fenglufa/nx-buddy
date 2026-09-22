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
Name: "desktopicon"; Description: "创建桌面快捷方式（双击即开主页）"

[Icons]
Name: "{autodesktop}\NX 小助手"; Filename: "{app}\tray\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\tray\{#MyAppExeName}"; Description: "启动 NX 小助手托盘"; Flags: postinstall nowait skipifsilent

[Code]
var
  DeployMsg: String;
  DeployFailed: Boolean;

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
      DeployMsg := 'NX 插件已合并部署进 ' + Ugii + '\startup（只复制、未删他人文件）。' + #13#10 +
                   '保存并关闭当前工作部件后重启 NX 即生效。';
  end
  else if CurStep = ssDone then
  begin
    // 静默/IT 推送不打扰；向导模式把部署结果明说，成功也提醒重启 NX。
    if not WizardSilent and (DeployMsg <> '') then
      if DeployFailed then
        MsgBox(DeployMsg, mbError, MB_OK)
      else
        MsgBox(DeployMsg, mbInformation, MB_OK);
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
