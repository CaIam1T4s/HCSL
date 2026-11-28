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
en.RuntimeSummary=Runtime:
zh.RuntimeSummary=运行时：
en.ReadyInstall=Ready to install
zh.ReadyInstall=准备安装
en.ReadyUpdate=Ready to update
zh.ReadyUpdate=准备更新
en.InstallDescription=Setup is ready to install HCSL Host.
zh.InstallDescription=安装程序已准备好安装 HCSL Host。
en.UpdateDescription=Setup is ready to update the existing HCSL Host installation.
zh.UpdateDescription=安装程序已准备好更新现有的 HCSL Host。
en.InstallAction=&Install
zh.InstallAction=安装(&I)
en.UpdateAction=&Update
zh.UpdateAction=更新(&U)
en.InstallHint=Click Install to continue.
zh.InstallHint=点击「安装」继续。
en.UpdateHint=Click Update to continue.
zh.UpdateHint=点击「更新」继续。
en.BackHint=Click Back to review or change your choices.
zh.BackHint=如需查看或修改选项，请点击「上一步」。
en.OpenHost=Open HCSL Host
zh.OpenHost=打开 HCSL Host

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

[Run]
Filename: "{app}\HCSL.Host.exe"; Description: "{cm:OpenHost}"; WorkingDir: "{app}"; Flags: postinstall nowait skipifsilent

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

function RuntimeError(): String;
begin
  Result := '';
#include RuntimeChecksFile
end;

function CompatiblePreviousMode(): String;
var
  Mode: String;
begin
  Result := '';
  if CompareText(GetPreviousData('ManagedDir', ''), NormalizeInstallDir(WizardDirValue())) <> 0 then Exit;
  Mode := GetPreviousData('RuntimeMode', '');
  if (Mode = 'bundled') or ((Mode = 'system') and (RuntimeError() = '')) then
    Result := Mode;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
var
  Mode: String;
begin
  Result := False;
  if PageID <> wpSelectComponents then Exit;
  Mode := CompatiblePreviousMode();
  Result := Mode <> '';
  if Result then WizardSelectComponents(Mode);
end;

function IsManagedUpdate(): Boolean;
begin
  Result := (GetPreviousData('RuntimeMode', '') <> '') and
    (CompareText(GetPreviousData('ManagedDir', ''), NormalizeInstallDir(WizardDirValue())) = 0);
end;

procedure CurPageChanged(CurPageID: Integer);
var
  MessageText: String;
begin
  if CurPageID = wpReady then begin
    WizardForm.PageDescriptionLabel.Left := WizardForm.PageNameLabel.Left;
    if IsManagedUpdate() then begin
      WizardForm.PageNameLabel.Caption := CustomMessage('ReadyUpdate');
      WizardForm.PageDescriptionLabel.Caption := CustomMessage('UpdateDescription');
      WizardForm.NextButton.Caption := CustomMessage('UpdateAction');
      MessageText := CustomMessage('UpdateHint');
    end else begin
      WizardForm.PageNameLabel.Caption := CustomMessage('ReadyInstall');
      WizardForm.PageDescriptionLabel.Caption := CustomMessage('InstallDescription');
      WizardForm.NextButton.Caption := CustomMessage('InstallAction');
      MessageText := CustomMessage('InstallHint');
    end;
    if WizardForm.BackButton.Visible then
      MessageText := MessageText + ' ' + CustomMessage('BackHint');
    WizardForm.ReadyLabel.Caption := MessageText;
  end;
  if (CurPageID = wpSelectComponents) and (GetPreviousData('RuntimeMode', '') = 'system') and
    (CompareText(GetPreviousData('ManagedDir', ''), NormalizeInstallDir(WizardDirValue())) = 0) then begin
    MessageText := RuntimeError();
    if MessageText <> '' then
      SuppressibleMsgBox(MessageText, mbInformation, MB_OK, IDOK);
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  ModeDescription: String;
begin
  if WizardIsComponentSelected('system') then
    ModeDescription := CustomMessage('SystemMode')
  else
    ModeDescription := CustomMessage('BundledMode');
  Result := MemoDirInfo + NewLine + NewLine + CustomMessage('RuntimeSummary') + NewLine + Space + ModeDescription;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Mode: String;
begin
  Mode := CompatiblePreviousMode();
  if Mode <> '' then WizardSelectComponents(Mode);
  Result := '';
  if CompareText(WizardDirValue(), NormalizeInstallDir(WizardDirValue())) <> 0 then
    Result := CustomMessage('InvalidDir')
  else if not ProgramsClosed() then
    Result := CustomMessage('ClosePrograms')
  else if WizardIsComponentSelected('system') then
    Result := RuntimeError();
end;
