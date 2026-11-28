; Compile via host-setup.ps1 with the two validated Windows package layouts.
[Setup]
AppId=HCSL.Host
AppName=HCSL Host
AppVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\HCSL_Host
AppendDefaultDirName=no
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

[Types]
Name: bundled; Description: "Bundled .NET runtime (recommended)"
Name: system; Description: "Use installed x64 .NET 10"

[Components]
Name: bundled; Description: "Bundled .NET runtime"; Types: bundled; Flags: fixed
Name: system; Description: "Use installed x64 .NET 10"; Types: system; Flags: fixed

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
    SuppressibleMsgBox('Stop any server or download, then exit HCSL Host and Daemon before uninstalling.', mbError, MB_OK, IDOK);
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
  Versions: TArrayOfString;
  I, Major, Minor, Patch, RequiredPatch, Separator: Integer;
  Version, Rest: String;
begin
  Result := False;
  RequiredPatch := StrToIntDef(Copy(Minimum, 6, Length(Minimum)), -1);
  if RequiredPatch < 0 then Exit;
  if not RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\' + Framework, Versions) then Exit;
  for I := 0 to GetArrayLength(Versions) - 1 do begin
    Version := Versions[I];
    Separator := Pos('.', Version);
    if Separator = 0 then Continue;
    Major := StrToIntDef(Copy(Version, 1, Separator - 1), -1);
    Rest := Copy(Version, Separator + 1, Length(Version));
    Separator := Pos('.', Rest);
    if Separator = 0 then Continue;
    Minor := StrToIntDef(Copy(Rest, 1, Separator - 1), -1);
    Patch := StrToIntDef(Copy(Rest, Separator + 1, Length(Rest)), -1);
    if (Major = 10) and (Minor = 0) and (Patch >= RequiredPatch) then begin
      Result := True;
      Exit;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(WizardDirValue(), NormalizeInstallDir(WizardDirValue())) <> 0 then
    Result := 'The installation directory must end in HCSL_Host.'
  else if not ProgramsClosed() then
    Result := 'Stop any server or download, then exit HCSL Host and Daemon before installing.'
  else if WizardIsComponentSelected('system') then begin
#include RuntimeChecksFile
  end;
end;
