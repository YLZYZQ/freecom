; FreeCom 安装脚本（Inno Setup 6，传统向导：可选安装位置/快捷方式）
; 编译：ISCC.exe installer\FreeCom.iss （产物输出到 dist\）
; 静默安装示例：FreeCom_vX_Setup.exe /VERYSILENT /DIR="D:\Tools\FreeCom"
; 静默卸载：unins000.exe /VERYSILENT

#define MyAppName "FreeCom 免费串口调试助手"
#define MyAppNameEn "FreeCom"
#define MyAppVersion "0.2.1"
#define MyAppPublisher "YLZYZQ"
#define MyAppExeName "FreeCom.App.exe"
#define MyAppAssocName MyAppName + " 配置文件"
#define MyAppAssocExt ".freecom"

[Setup]
AppId={{8B7A2C41-9F5E-4D63-B2A8-6E1D0C9F4A77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/YLZYZQ/freecom
AppSupportURL=https://github.com/YLZYZQ/freecom/issues
DefaultDirName={autopf}\{#MyAppNameEn}
DefaultGroupName={#MyAppName}
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
OutputDir=..\dist
OutputBaseFilename=FreeCom_v{#MyAppVersion}_win64_Setup
SetupIconFile=..\src\FreeCom.App\freecom.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; 可提权装 Program Files；加 /CURRENTUSER 参数也可装用户目录（免 UAC）
PrivilegesRequiredOverridesAllowed=dialog commandline
LicenseFile=..\LICENSE
; 版本信息
VersionInfoVersion={#MyAppVersion}
VersionInfoProductVersion={#MyAppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\publish\FreeCom\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 卸载清理本目录内运行期产物（用户数据 %LOCALAPPDATA%\FreeCom\settings.json 保留）
Type: filesandordirs; Name: "{app}\runtimes"

[Messages]
; 覆盖内置中文文案中的产品占位（保证名称一致）
chinesesimplified.SetupAppTitle=安装 - {#MyAppName}
chinesesimplified.UninstallAppTitle=卸载 - {#MyAppName}
