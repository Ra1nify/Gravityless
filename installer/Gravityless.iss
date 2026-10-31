; ============================================================================
;  Gravityless — Inno Setup 6 script
;
;  Build (per architecture):
;    dotnet publish src\Gravityless.App\Gravityless.App.csproj ^
;      -c Release -r win-x64 --self-contained false -o artifacts\win-x64
;    iscc /DAppArch=x64 /DPublishDir=artifacts\win-x64 installer\Gravityless.iss
;
;    dotnet publish src\Gravityless.App\Gravityless.App.csproj ^
;      -c Release -r win-arm64 --self-contained false -o artifacts\win-arm64
;    iscc /DAppArch=arm64 /DPublishDir=artifacts\win-arm64 installer\Gravityless.iss
; ============================================================================

#ifndef AppArch
  #define AppArch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\win-" + AppArch
#endif

#define AppName        "Gravityless"
#define AppPublisher   "Gravityless"
#define AppExeName     "Gravityless.App.exe"
#ifndef AppVersion
  #define AppVersion "26.10.99"
#endif
#define AppUrl         "https://github.com/C-F0x/Gravityless"

; Map the architecture to the values Inno expects.
#if AppArch == "arm64"
  #define ArchAllowed  "arm64"
  #define ArchMode     "arm64"
#else
  #define ArchAllowed  "x64compatible"
  #define ArchMode     "x64compatible"
#endif

[Setup]
AppId={{8F3C1A2E-6D4B-4E7A-9C51-2B8E4F7D1A63}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; Per-machine install keeps the protocol handler valid for all users;
; PrivilegesRequired=lowest would force a per-user install instead.
PrivilegesRequired=admin
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#ArchMode}

OutputDir=..\artifacts\installer
OutputBaseFilename={#AppName}-{#AppVersion}-{#AppArch}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

; Transparent multi-size icon (16-256), so the wizard title bar and the
; Add/Remove Programs entry match the app shortcut.
SetupIconFile=..\src\Gravityless.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}

; .NET 8 Desktop Runtime is a documented prerequisite; checked in InitializeSetup.
MinVersion=10.0.17763

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; The published application payload.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; antigravity:// must resolve to Gravityless so OAuth callbacks reach the capture sandbox.
; The app re-registers this at startup; doing it here makes the first launch work offline.
Root: HKA; Subkey: "Software\Classes\antigravity"; ValueType: string; ValueName: ""; ValueData: "URL:antigravity Protocol"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\antigravity"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\antigravity\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKA; Subkey: "Software\Classes\antigravity\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Runtime output only. data\ (accounts, projects) and core\ (Antigravity engine) are
; handled by the optional task below, because losing them forces a full re-download.
Type: filesandordirs; Name: "{app}\locales"
Type: filesandordirs; Name: "{app}\Assets"

[Code]
{ ---------------------------------------------------------------------------
  Architecture guard.
[Code]
{ ---------------------------------------------------------------------------
  Architecture guard.

  ArchitecturesAllowed already blocks a mismatched machine, but Inno reports it
  as a generic "wrong Windows version" error. This check names the mismatch and
  points at the correct download.
  --------------------------------------------------------------------------- }
function InitializeSetup(): Boolean;
var
  IsArm: Boolean;
  ExpectedArch: String;
begin
  IsArm := IsArm64;

  { Passed in from build.ps1 as /DAppArch. }
  ExpectedArch := '{#AppArch}';

  if (ExpectedArch = 'arm64') and (not IsArm) then
  begin
    MsgBox('This is the ARM64 build of Gravityless.' + #13#10 + #13#10 +
           'Your machine is x64. Download the x64 installer instead.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if (ExpectedArch = 'x64') and IsArm then
  begin
    MsgBox('This is the x64 build of Gravityless.' + #13#10 + #13#10 +
           'Your machine is ARM64. Download the ARM64 installer instead.',
           mbError, MB_OK);
    Result := False;
    Exit;
  end;

  Result := True;
end;

{ ---------------------------------------------------------------------------
  Uninstall: core\ and data\ are large and survive upgrades, so deleting them
  is opt-in. The uninstaller cannot add wizard pages, so it asks via MsgBox.
  --------------------------------------------------------------------------- }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;

  AppDir := ExpandConstant('{app}');

  if DirExists(AppDir + '\core') then
  begin
    if MsgBox(
      'Also delete the Antigravity engine (core folder)?' + #13#10 + #13#10 +
      'Deleting it means a full re-download on the next install.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(AppDir + '\core', True, True, True);
  end;

  if DirExists(AppDir + '\data') then
  begin
    if MsgBox(
      'Also delete all accounts, projects and settings (data folder)?' + #13#10 + #13#10 +
      'This cannot be undone.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(AppDir + '\data', True, True, True);
  end;
end;
