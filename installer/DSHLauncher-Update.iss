#define AppName "DSH Launcher Update"
#define AppVersion "0.1.6"
#define RepoRoot AddBackslash(SourcePath) + ".."

[Setup]
AppId={{08403760-297B-4F56-9570-6CF4CA4C4447}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=DSH Launcher
DefaultDirName={code:GetLauncherDir}
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#RepoRoot}\artifacts\updater
OutputBaseFilename=DSHLauncher-Update-{#AppVersion}-x64
SetupIconFile={#RepoRoot}\src\DshLauncher\Assets\whale.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
DisableDirPage=yes
DisableProgramGroupPage=yes
CreateUninstallRegKey=no
Uninstallable=no
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "{#RepoRoot}\installer\languages\ChineseSimplified.isl"

[Files]
Source: "{#RepoRoot}\artifacts\app\DshLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion

[Run]

[Code]
function GetLauncherDir(Param: String): String;
var
  InstallLocation: String;
begin
  if RegQueryStringValue(
       HKLM64,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{6D269B8A-6CA4-4812-A680-C4882E7866EF}_is1',
       'InstallLocation',
       InstallLocation) and (InstallLocation <> '') then
    Result := InstallLocation
  else
    Result := ExpandConstant('{autopf}\DSH Launcher');
end;
