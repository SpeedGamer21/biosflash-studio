; BiosFlash Studio — Windows installer (Inno Setup 6)
; Build:  build.bat installer   (or: ISCC packaging\windows\BiosFlashStudio.iss after build.bat portable)

#define AppName "BiosFlash Studio"
#define AppVersion "2.1.0"
#define AppExe "BiosFlashStudio.exe"
#define Root "..\.."

[Setup]
AppId={{6A4B8E2C-1F3D-4C5A-9B7E-2D8F0A1C3E57}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=BiosFlash Studio
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; lets the user pick "install for me only" (no admin) or "for all users"
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#Root}\dist\installers
OutputBaseFilename=BiosFlashStudio-{#AppVersion}-win-x64-setup
SetupIconFile={#Root}\src\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
InfoAfterFile={#Root}\packaging\windows\after-install.txt

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Root}\dist\portable\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{#AppName} (demo mode)"; Filename: "{app}\{#AppExe}"; Parameters: "--demo"; Comment: "Try the app with the built-in simulator"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
