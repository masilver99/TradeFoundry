#define MyAppName "TradeFoundry"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "TradeFoundry"
#define MyAppExeName "TradeFoundry.Desktop.exe"
#ifndef PublishDir
#define PublishDir "..\artifacts\windows\publish"
#endif
#ifndef InstallerOutputDir
#define InstallerOutputDir "..\artifacts\windows\installer"
#endif

[Setup]
AppId={{D9C9D5B7-1C51-4E38-8E7B-CEB22B6F9C15}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\TradeFoundry
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#InstallerOutputDir}
OutputBaseFilename=TradeFoundry-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes
CloseApplications=yes
CloseApplicationsFilter=TradeFoundry*.exe
RestartApplications=no
; The tray helper and the server it starts each hold one of these mutexes.
AppMutex=TradeFoundry.Desktop,TradeFoundry.DesktopShell

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[InstallDelete]
; Remove top-level binaries from the previous version (including the retired WPF/WebView2
; shell) before installing the new ones. Journal data never lives directly in {app}.
Type: files; Name: "{app}\*.dll"
Type: files; Name: "{app}\TradeFoundry*.exe"
Type: files; Name: "{app}\createdump.exe"
Type: files; Name: "{app}\*.json"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TradeFoundry"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\TradeFoundry"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; The tray menu's "Start at sign-in" option writes this value; remove it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TradeFoundry"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Start TradeFoundry"; Flags: nowait postinstall skipifsilent

[Code]
var
  DataDirectoryPage: TInputDirWizardPage;

function JsonEscape(const Value: string): string;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
  StringChangeEx(Result, #13, '\r', True);
  StringChangeEx(Result, #10, '\n', True);
end;

function DataDirectoryConfigPath: string;
begin
  Result := ExpandConstant('{localappdata}\TradeFoundry\appsettings.user.json');
end;

procedure InitializeWizard;
begin
  DataDirectoryPage := CreateInputDirPage(
    wpSelectDir,
    'Journal data directory',
    'Choose where TradeFoundry should store its database and review attachments.',
    'Existing journal data in this directory will be used in place. The installer will not delete or overwrite it.',
    False,
    'TradeFoundry data');
  DataDirectoryPage.Add('');
  DataDirectoryPage.Values[0] := GetPreviousData(
    'DataDirectory',
    ExpandConstant('{localappdata}\TradeFoundry\Data'));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  DataDirectory: string;
begin
  Result := True;
  if CurPageID <> DataDirectoryPage.ID then
    Exit;

  DataDirectory := RemoveBackslashUnlessRoot(DataDirectoryPage.Values[0]);
  if DataDirectory = '' then
  begin
    MsgBox('Choose a journal data directory.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  if CompareText(DataDirectory, ExpandConstant('{app}')) = 0 then
  begin
    MsgBox('Journal data must be outside the application install directory.', mbError, MB_OK);
    Result := False;
    Exit;
  end;

  DataDirectoryPage.Values[0] := DataDirectory;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDirectory: string;
  ConfigJson: string;
begin
  if CurStep <> ssPostInstall then
    Exit;

  ConfigDirectory := ExpandConstant('{localappdata}\TradeFoundry');
  ForceDirectories(ConfigDirectory);
  ConfigJson := '{' + #13#10 +
    '  "Storage": {' + #13#10 +
    '    "DataDirectory": "' + JsonEscape(DataDirectoryPage.Values[0]) + '"' + #13#10 +
    '  }' + #13#10 +
    '}' + #13#10;
  SaveStringToFile(DataDirectoryConfigPath, ConfigJson, False);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'DataDirectory', DataDirectoryPage.Values[0]);
end;
