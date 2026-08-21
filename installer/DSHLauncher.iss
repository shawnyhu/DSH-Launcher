#define AppName "DSH Launcher"
#define AppVersion "0.1.8"
#define AppPublisher "DSH Launcher"
#define NodeVersion "24.19.0"
#define RepoRoot AddBackslash(SourcePath) + ".."

[Setup]
AppId={{6D269B8A-6CA4-4812-A680-C4882E7866EF}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\DSH Launcher
DefaultGroupName=DSH Launcher
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#RepoRoot}\artifacts\installer
OutputBaseFilename=DSHLauncher-Windows-Setup-{#AppVersion}-x64
SetupIconFile={#RepoRoot}\src\DshLauncher\Assets\whale.ico
UninstallDisplayIcon={app}\DshLauncher.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

[Languages]
Name: "chinesesimp"; MessagesFile: "{#RepoRoot}\installer\languages\ChineseSimplified.isl"

[Tasks]
Name: "autostart"; Description: "开机时自动启动 DSH Launcher"; GroupDescription: "启动选项:"; Flags: checkedonce
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式:"

[Files]
Source: "{#RepoRoot}\artifacts\app\DshLauncher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\installer\cache\node-v{#NodeVersion}-x64.msi"; DestDir: "{tmp}"; DestName: "node-lts-x64.msi"; Flags: deleteafterinstall; Check: NeedNode

[Icons]
Name: "{group}\DSH Launcher"; Filename: "{app}\DshLauncher.exe"
Name: "{autodesktop}\DSH Launcher"; Filename: "{app}\DshLauncher.exe"; Tasks: desktopicon

[Run]
Filename: "{sys}\msiexec.exe"; Parameters: "/i ""{tmp}\node-lts-x64.msi"" /qn /norestart"; StatusMsg: "正在安装 Node.js 24 LTS…"; Check: NeedNode; Verb: "runas"; Flags: shellexec waituntilterminated
Filename: "{app}\DshLauncher.exe"; Parameters: "{code:GetDshInstallArguments}"; StatusMsg: "正在安装 DeepSeek Harness…"; Flags: waituntilterminated runhidden runasoriginaluser
Filename: "{app}\DshLauncher.exe"; Parameters: "--set-autostart on"; StatusMsg: "正在配置开机自启…"; Tasks: autostart; Flags: waituntilterminated runhidden runasoriginaluser

[UninstallRun]
Filename: "{app}\DshLauncher.exe"; Parameters: "--set-autostart off"; Flags: runhidden; RunOnceId: "RemoveAutoStart"
Filename: "{cmd}"; Parameters: "/d /s /c ""taskkill /IM DshLauncher.exe /F >nul 2>nul"""; Flags: runhidden; RunOnceId: "StopLauncher"

[Code]
var
  InstallModePage: TInputOptionWizardPage;
  ManagedPathPage: TInputDirWizardPage;
  HomePathPage: TInputDirWizardPage;

procedure InitializeWizard;
begin
  InstallModePage := CreateInputOptionPage(
    wpSelectDir,
    '选择 DSH 安装方式',
    '默认将 @deepseek-ai/dsh 安装到 npm 全局范围。',
    '独立安装可以和其他 DSH 版本并存。',
    True,
    False);
  InstallModePage.Add('npm 全局安装（推荐）');
  InstallModePage.Add('Launcher 管理的独立安装');
  InstallModePage.SelectedValueIndex := 0;

  ManagedPathPage := CreateInputDirPage(
    InstallModePage.ID,
    '选择独立 DSH 安装目录',
    '该目录只存放 DSH 程序包。',
    '下一步单独选择 DSH_HOME，程序包和数据目录互不绑定。',
    False,
    '');
  ManagedPathPage.Add('DSH 程序包目录：');
  ManagedPathPage.Values[0] := ExpandConstant('{localappdata}\DSHLauncher\runtimes\default');

  HomePathPage := CreateInputDirPage(
    ManagedPathPage.ID,
    '选择 DSH_HOME',
    '该目录保存 DSH 配置、会话和工作数据。',
    '默认使用当前用户目录下的 .dsh；卸载或重装 DSH 时不会删除这里的数据。',
    False,
    '');
  HomePathPage.Add('DSH_HOME 路径：');
  HomePathPage.Values[0] := ExpandConstant('{%USERPROFILE}\.dsh');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = ManagedPathPage.ID) and (InstallModePage.SelectedValueIndex = 0);
end;

function VersionMajor(const VersionText: String): Integer;
var
  Separator: Integer;
begin
  Separator := Pos('.', VersionText);
  if Separator > 0 then
    Result := StrToIntDef(Copy(VersionText, 1, Separator - 1), 0)
  else
    Result := StrToIntDef(VersionText, 0);
end;

function VersionMinor(const VersionText: String): Integer;
var
  FirstSeparator: Integer;
  Remaining: String;
  SecondSeparator: Integer;
begin
  FirstSeparator := Pos('.', VersionText);
  if FirstSeparator = 0 then
  begin
    Result := 0;
    Exit;
  end;
  Remaining := Copy(VersionText, FirstSeparator + 1, Length(VersionText));
  SecondSeparator := Pos('.', Remaining);
  if SecondSeparator > 0 then
    Remaining := Copy(Remaining, 1, SecondSeparator - 1);
  Result := StrToIntDef(Remaining, 0);
end;

function IsCompatibleNode(const FileName: String): Boolean;
var
  VersionText: String;
  MajorVersion: Integer;
  MinorVersion: Integer;
begin
  Result := False;
  if not FileExists(FileName) then
    Exit;
  if not GetVersionNumbersString(FileName, VersionText) then
    Exit;
  MajorVersion := VersionMajor(VersionText);
  MinorVersion := VersionMinor(VersionText);
  Result := (MajorVersion >= 24) or ((MajorVersion = 22) and (MinorVersion >= 19));
end;

function NeedNode: Boolean;
begin
  Result :=
    not IsCompatibleNode(ExpandConstant('{pf64}\nodejs\node.exe')) and
    not IsCompatibleNode(ExpandConstant('{pf32}\nodejs\node.exe'));
end;

function GetDshInstallArguments(Param: String): String;
begin
  if InstallModePage.SelectedValueIndex = 0 then
    Result := '--install-dsh global latest --dsh-home "' + HomePathPage.Values[0] + '"'
  else
    Result := '--install-dsh managed "' + ManagedPathPage.Values[0] + '" latest --dsh-home "' + HomePathPage.Values[0] + '"';
end;
