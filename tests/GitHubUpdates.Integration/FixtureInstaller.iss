; Isolated test registration. Never use the production AppId, desktop or Start-menu names.
[Setup]
AppId={#FixtureId}
AppName=xrMPE GitHub Update Test Fixture
AppVersion=0.3.1
DefaultDirName={#FixtureDirectory}
UsePreviousAppDir=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=xrMPE GitHub Update Test Fixture
OutputDir={#FixtureOutput}
OutputBaseFilename=TestSetup
Compression=lzma2/fast
SolidCompression=yes
DisableProgramGroupPage=yes

[Files]
Source: "{#FixturePublication}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{app}\Existing Launcher"; Filename: "{app}\Xrmpe.Launcher.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\launcher.ico"

[UninstallDelete]
Type: files; Name: "{app}\.xrmpe-launcher.lock"
