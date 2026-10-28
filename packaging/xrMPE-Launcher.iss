; Inno Setup 6.3+ project. Compiling this file is required to obtain an installer EXE.
; In the distributed preview this script is adjacent to app/.
#ifndef PublishDir
  #define PublishDir SourcePath + "app"
#endif
#ifndef InstallerOutput
  #define InstallerOutput SourcePath
#endif
#ifndef AppVersion
  #define AppVersion "0.3.1"
#endif
#ifndef InstallerName
  #define InstallerName "xrMPE-Launcher-Setup-" + AppVersion
#endif

[Setup]
AppId={{EB475631-868E-40C0-B134-0F6DB93D551A}
AppName=xrMPE Launcher
AppVersion={#AppVersion}
AppPublisher=xrMPE Custom Launcher
DefaultDirName={code:UpgradeDefaultDirectory}
UsePreviousAppDir=yes
UsePreviousPrivileges=yes
DefaultGroupName=xrMPE Launcher
DisableDirPage=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#InstallerOutput}
OutputBaseFilename={#InstallerName}
SetupIconFile={#PublishDir}\Assets\launcher.ico
UninstallDisplayIcon={app}\Assets\launcher.ico
Uninstallable=yes
CreateUninstallRegKey=yes
UninstallDisplayName=xrMPE Launcher
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AppMutex=Local\xrMPE.CustomLauncher.Running
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Only the self-contained launcher publication. No game, profile or updater staging files.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourcePath}Repair-LauncherShortcuts.ps1"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\xrMPE Launcher\xrMPE Launcher"; Filename: "{app}\Xrmpe.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\launcher.ico"; AppUserModelID: "xrMPE.CustomLauncher"
Name: "{code:UpgradeDesktopFolder}\{code:UpgradeDesktopName}"; Filename: "{app}\Xrmpe.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\launcher.ico"; AppUserModelID: "xrMPE.CustomLauncher"; Check: UpgradeNeedsDesktopIcon

[Run]
Filename: "{app}\Xrmpe.Launcher.exe"; Parameters: "--choose-game"; Description: "{cm:LaunchProgram,xrMPE Launcher}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

; This zero-byte coordination file belongs only to the self-updater.
[UninstallDelete]
Type: files; Name: "{app}\.xrmpe-launcher.lock"

; All other files/shortcuts are removed only when recorded at installation.
; LOCALAPPDATA\Xrmpe.CustomLauncher, selected game roots, saves and configs stay intact.
[Code]
#include "UpgradeDetection.iss"
function IsGameFolder(const Folder: String): Boolean;
begin
  Result := FileExists(AddBackslash(Folder) + 'bin\xrEngine.exe') or
            FileExists(AddBackslash(Folder) + 'game\bin\xrEngine.exe') or
            FileExists(AddBackslash(Folder) + 'xrEngine.exe');
end;

function CheckInstallFolder(const Selected: String): String;
var
  Folder, Parent: String;
begin
  Result := '';
  Folder := RemoveBackslash(ExpandFileName(Selected));
  while Length(Folder) > 3 do
  begin
    if IsGameFolder(Folder) then
    begin
      Result := 'Install the launcher in a separate folder outside the game. Game files and profiles must remain separate.';
      Exit;
    end;
    Parent := RemoveBackslash(ExtractFileDir(Folder));
    if Parent = Folder then Break;
    Folder := Parent;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var ErrorText: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  { Silent installs are validated by PrepareToInstall without an interactive MsgBox. }
  if WizardSilent then Exit;
  ErrorText := CheckInstallFolder(WizardDirValue);
  if ErrorText = '' then
    if not UpgradeConfirmDirectory then
    begin
      Result := False;
      Exit;
    end;
  if ErrorText <> '' then
  begin
    MsgBox(ErrorText, mbError, MB_OK);
    Result := False;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := CheckInstallFolder(WizardDirValue);
  if Result = '' then Result := UpgradePrepare;
end;
