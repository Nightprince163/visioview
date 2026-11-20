; VisioView 安装脚本（Inno Setup）
; 用法：用 Inno Setup 打开本文件并编译，需先执行 publish.ps1 生成单文件 exe。
; 安装程序会以机器级（HKLM）注册 .vsdx 文件关联（需要管理员权限）。

[Setup]
AppName=VisioView
AppVersion=1.5.2
AppPublisher=VisioView
DefaultDirName={autopf}\VisioView
DefaultGroupName=VisioView
OutputDir=installer\out
OutputBaseFilename=VisioView_Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
SetupIconFile=..\assets\VisioView.ico
UninstallDisplayIcon={app}\VisioView.exe,0

[Files]
; 单文件自包含发布产物
Source="src\VisioView\bin\Release\net10.0-windows\win-x64\publish\VisioView.exe"; DestDir="{app}"; Flags=ignoreversion

[Icons]
Name: "{group}\VisioView"; Filename: "{app}\VisioView.exe"
Name: "{autodesktop}\VisioView"; Filename: "{app}\VisioView.exe"; Tasks: desktopicon

[Tasks]
Name: desktopicon; Description: "创建桌面快捷方式"; GroupDescription: "额外任务:"

[Registry]
; 机器级 .vsdx 关联
Root: HKLM; Subkey: "Software\Classes\.vsdx"; ValueType: string; ValueName: ""; ValueData: "VisioView.vsdxfile"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\VisioView.vsdxfile"; ValueType: string; ValueName: ""; ValueData: "Visio 绘图 (.vsdx)"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\VisioView.vsdxfile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\VisioView.exe,0"
Root: HKLM; Subkey: "Software\Classes\VisioView.vsdxfile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VisioView.exe"" ""%1"""

[Run]
Filename: "{app}\VisioView.exe"; Description: "启动 VisioView"; Flags: nowait postinstall skipifsilent
