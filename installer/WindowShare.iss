; ============================================================
; WindowShare（窗享）安装包脚本 - Inno Setup 6
; 构建：先运行 scripts/build.ps1 生成 publish 输出，再执行
;   ISCC.exe installer\WindowShare.iss
; ============================================================

#define MyAppName "WindowShare"
#define MyAppChineseName "窗享"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "WindowShare"
#define MyAppExeName "WindowShare.Host.exe"
#define PublishDir "..\dist\publish"

[Setup]
AppId={{7C3B8F52-9A64-4C2E-B7A5-3D0F1E2A5B90}
AppName={#MyAppName} ({#MyAppChineseName})
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\WindowShare
DefaultGroupName={#MyAppChineseName}
DisableProgramGroupPage=yes
; 只支持 Windows 10 1903+（WGC 最低要求）
MinVersion=10.0.18362
OutputDir=output
OutputBaseFilename=WindowShare-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 无需管理员权限（per-user 安装），数据写入 %APPDATA%\WindowShare
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\WindowShare.Host.exe

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; self-contained 发布输出（含 .NET 运行时）
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppChineseName} Host（共享端）"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{#MyAppChineseName} Viewer（观看端）"; Filename: "{app}\WindowShare.Viewer.exe"
Name: "{autodesktop}\{#MyAppChineseName} Host"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "启动 {#MyAppChineseName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载时保留 %APPDATA%\WindowShare（白名单/设置），仅删除程序目录
Type: filesandordirs; Name: "{app}"

[Messages]
WelcomeLabel2=这将安装 [name/ver]。%n%n只读屏幕/窗口共享：Host 端捕获并编码，Viewer 端解码观看。%n%n不含任何远程控制功能。
