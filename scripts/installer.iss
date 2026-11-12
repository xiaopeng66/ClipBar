; ClipBar installer (Inno Setup 6).
; Build with scripts\build-installer.ps1, which passes the source exe, the icon and the version.
; Per-user by default: {autopf} resolves to %LOCALAPPDATA%\Programs when PrivilegesRequired=lowest,
; so a normal user can install without UAC. "Install for all users" stays available in the dialog.
;
; The uninstaller deliberately keeps %LOCALAPPDATA%\ClipBar (history, images, settings): removing
; the program must not destroy the user's clipboard history.

#ifndef SourceExe
  #error Pass /DSourceExe=<path to ClipBar.exe> to ISCC.
#endif
#ifndef SourceIcon
  #error Pass /DSourceIcon=<path to app.ico> to ISCC.
#endif
#ifndef MyVersion
  #define MyVersion "1.1.0"
#endif

[Setup]
AppId={{9F1C2E47-6B3A-4C58-9E2D-7B4A1F8C5D30}
AppName=ClipBar
AppVersion={#MyVersion}
AppVerName=ClipBar {#MyVersion}
AppPublisher=ClipBar contributors
AppPublisherURL=https://clipbar.us.ci
AppSupportURL=https://clipbar.us.ci
AppUpdatesURL=https://github.com/xiaopeng66/ClipBar/releases/latest
DefaultDirName={autopf}\ClipBar
DefaultGroupName=ClipBar
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
OutputBaseFilename=ClipBar-Setup-{#MyVersion}
SetupIconFile={#SourceIcon}
UninstallDisplayIcon={app}\ClipBar.exe
UninstallDisplayName=ClipBar
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "{#SourcePath}\installer\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:AutoStartProgram,ClipBar}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "ClipBar.exe"; Flags: ignoreversion
Source: "{#SourceIcon}"; DestDir: "{app}"; DestName: "ClipBar.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\ClipBar"; Filename: "{app}\ClipBar.exe"; WorkingDir: "{app}"
Name: "{group}\卸载 ClipBar"; Filename: "{uninstallexe}"
Name: "{autodesktop}\ClipBar"; Filename: "{app}\ClipBar.exe"; Tasks: desktopicon

[Registry]
; Same Run value the app itself writes, so both agree; the app reconciles it on next launch.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "ClipBar"; ValueData: """{app}\ClipBar.exe"""; Flags: uninsdeletevalue; Tasks: startup
; Legacy value from the pre-rename build.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "Clipboard11"; Flags: deletevalue

[Run]
Filename: "{app}\ClipBar.exe"; Description: "{cm:LaunchProgram,ClipBar}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Only the install directory; %LOCALAPPDATA%\ClipBar stays untouched by design.
Type: filesandordirs; Name: "{app}"
