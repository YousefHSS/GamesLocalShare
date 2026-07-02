; Inno Setup Script for GamesLocalShare
; Download Inno Setup from: https://jrsoftware.org/isinfo.php

#define MyAppName "Games Local Share"
#define MyAppVersion "2.0.0"
#define MyAppPublisher "YousefHSS"
#define MyAppURL "https://github.com/YousefHSS/GamesLocalShare"
#define MyAppExeName "GamesLocalShare.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
LicenseFile=LICENSE.txt
OutputDir=installer_output
OutputBaseFilename=GamesLocalShare-Setup-{#MyAppVersion}
SetupIconFile=Icons\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startupicon"; Description: "Start with Windows (minimized)"; GroupDescription: "Startup Options:"; Flags: unchecked

[Files]
Source: "bin\Release\net8.0\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "bin\Release\net8.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; NOTE: Don't use "Flags: ignoreversion" on any shared system files

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; Startup is handled by an elevated scheduled task (see [Run] --register-startup).
; Remove legacy Run-key entries so the app isn't launched twice at logon: the old
; installer wrote "{#MyAppName}" (with spaces), the app's fallback writes "GamesLocalShare".
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "{#MyAppName}"; ValueType: none; Flags: deletevalue; Tasks: startupicon
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "GamesLocalShare"; ValueType: none; Flags: deletevalue; Tasks: startupicon

[Run]
; Register the RunLevel=Highest logon task via the app's own helper mode. The installer
; is already elevated, so this shows no UAC prompt. Must run before the launch entry.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--register-startup"; Flags: runhidden waituntilterminated; Tasks: startupicon
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the startup task (and any Run-key fallback) on uninstall; runs before files
; are deleted and the uninstaller is elevated, so this is silent.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--unregister-startup"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterStartupTask"

[Code]
// Check if .NET 8 is installed (for framework-dependent builds)
function IsDotNet8Installed: Boolean;
var
  Success: Boolean;
  ResultCode: Integer;
begin
  Success := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := Success and (ResultCode = 0);
end;

function InitializeSetup: Boolean;
begin
  // Uncomment this for framework-dependent builds
  // if not IsDotNet8Installed then
  // begin
  //   MsgBox('.NET 8 Runtime is required. Please install it from https://dotnet.microsoft.com/download/dotnet/8.0', mbError, MB_OK);
  //   Result := False;
  // end
  // else
    Result := True;
end;
