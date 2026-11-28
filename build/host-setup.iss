; Compile via host-setup.ps1 with the two validated Windows package layouts.
[Setup]
AppId=HCSL.Host
AppName=HCSL Host
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\HCSL_Host
AppendDefaultDirName=yes
DirExistsWarning=no
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DisableProgramGroupPage=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayIcon={app}\HCSL.Host.exe
OutputDir={#OutputDir}
OutputBaseFilename=HCSL-Host-{#AppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes

[Languages]
Name: en; MessagesFile: "compiler:Default.isl"
Name: zh; MessagesFile: "{#ChineseMessagesFile}"

[CustomMessages]
en.BundledMode=Bundled .NET runtime (recommended)
zh.BundledMode=捆绑 .NET 运行时（推荐）
en.SystemMode=Use installed x64 .NET 10
zh.SystemMode=使用系统 x64 .NET 10
en.RuntimeMissing=Install x64 %1 %2 or a newer .NET 10 patch, or select the bundled runtime.
zh.RuntimeMissing=请安装 x64 %1 %2 或更新的 .NET 10 补丁版本，或选择捆绑运行时。
en.ClosePrograms=Stop any server or download, then exit HCSL Host and Daemon before continuing.
zh.ClosePrograms=请先停服并结束下载或校验，再退出 HCSL Host 和 Daemon。
en.InvalidDir=The installation directory must end in HCSL_Host.
zh.InvalidDir=安装目录必须以 HCSL_Host 结尾。

[Types]
Name: bundled; Description: "{cm:BundledMode}"
Name: system; Description: "{cm:SystemMode}"

[Components]
Name: bundled; Description: "{cm:BundledMode}"; Types: bundled; Flags: fixed
Name: system; Description: "{cm:SystemMode}"; Types: system; Flags: fixed

[Files]
Source: "{#BundledDir}\*"; DestDir: "{app}"; Components: bundled; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#SystemDir}\*"; DestDir: "{app}"; Components: system; Flags: recursesubdirs createallsubdirs ignoreversion

[InstallDelete]
#include CleanupFile

[Icons]
Name: "{userprograms}\HCSL Host"; Filename: "{app}\HCSL.Host.exe"; WorkingDir: "{app}"

[Code]
function ProgramsClosed(): Boolean;
var
  Locator, Services, Processes: Variant;
begin
  Result := False;
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT Name FROM Win32_Process WHERE Name = ''HCSL.Host.exe'' OR Name = ''HCSL.Daemon.exe''');
    Result := Processes.Count = 0;
  except
    Log('Could not verify that HCSL processes are closed.');
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := ProgramsClosed();
  if not Result then
    SuppressibleMsgBox(CustomMessage('ClosePrograms'), mbError, MB_OK, IDOK);
end;

function NormalizeInstallDir(Path: String): String;
begin
  Result := RemoveBackslashUnlessRoot(Path);
  if CompareText(ExtractFileName(Result), 'HCSL_Host') <> 0 then
    Result := AddBackslash(Result) + 'HCSL_Host';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  if CurPageID = wpSelectDir then
    WizardForm.DirEdit.Text := NormalizeInstallDir(WizardForm.DirEdit.Text);
  Result := True;
end;

function IsModeSwitch(): Boolean;
var
  PreviousMode: String;
begin
  PreviousMode := GetPreviousData('RuntimeMode', '');
  Result := (CompareText(GetPreviousData('ManagedDir', ''), ExpandConstant('{app}')) = 0) and
    (((PreviousMode = 'bundled') and WizardIsComponentSelected('system')) or
     ((PreviousMode = 'system') and WizardIsComponentSelected('bundled')));
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'ManagedDir', ExpandConstant('{app}'));
  if WizardIsComponentSelected('system') then
    SetPreviousData(PreviousDataKey, 'RuntimeMode', 'system')
  else
    SetPreviousData(PreviousDataKey, 'RuntimeMode', 'bundled');
end;

function HasRuntime(Framework, Minimum: String): Boolean;
var
  Entry: TFindRec;
  Patch, RequiredPatch: Integer;
  Root, Shared: String;
begin
  Result := False;
  RequiredPatch := StrToIntDef(Copy(Minimum, 6, Length(Minimum)), -1);
  if RequiredPatch < 0 then Exit;
  if not RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) then
    Root := ExpandConstant('{commonpf64}\dotnet');
  if not FileExists(AddBackslash(Root) + 'dotnet.exe') then Exit;
  Shared := AddBackslash(Root) + 'shared\' + Framework + '\';
  if FindFirst(Shared + '10.0.*', Entry) then
    try
      repeat
        Patch := StrToIntDef(Copy(Entry.Name, 6, Length(Entry.Name)), -1);
        if (Patch >= RequiredPatch) and FileExists(Shared + Entry.Name + '\' + Framework + '.deps.json') then begin
          Result := True;
          Exit;
        end;
      until not FindNext(Entry);
    finally
      FindClose(Entry);
    end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(WizardDirValue(), NormalizeInstallDir(WizardDirValue())) <> 0 then
    Result := CustomMessage('InvalidDir')
  else if not ProgramsClosed() then
    Result := CustomMessage('ClosePrograms')
  else if WizardIsComponentSelected('system') then begin
#include RuntimeChecksFile
  end;
end;
