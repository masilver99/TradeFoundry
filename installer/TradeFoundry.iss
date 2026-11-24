#define MyAppName "TradeFoundry"
; CI passes the release version (for example /DMyAppVersion=1.2.0-rc.1 /DMyAppNumericVersion=1.2.0).
#ifndef MyAppVersion
#define MyAppVersion "0.0.0-dev"
#endif
#ifndef MyAppNumericVersion
#define MyAppNumericVersion "0.0.0"
#endif
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
VersionInfoVersion={#MyAppNumericVersion}
VersionInfoProductTextVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
SetupIconFile={#PublishDir}\TradeFoundry.ico
UninstallDisplayIcon={app}\TradeFoundry.ico
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
; Held while Setup runs. A TradeFoundry launch during a silent update sees it and leaves the
; restart to Setup (see the tray's Program.cs).
SetupMutex=TradeFoundry.Setup
; No AppMutex directive: the running-app check lives in [Code] (EnsureAppClosed) so that an
; update started from the tray can wait for the tray to exit instead of failing silently.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[InstallDelete]
; Remove leftovers of the retired WPF/WebView2 shell. Only files the current build never ships
; are listed, so an update that fails partway can never leave TradeFoundry without its own files.
Type: files; Name: "{app}\Microsoft.Web.WebView2.*.dll"
Type: files; Name: "{app}\WebView2Loader.dll"
Type: files; Name: "{app}\Presentation*.dll"
Type: files; Name: "{app}\*_cor3.dll"
Type: files; Name: "{app}\DirectWriteForwarder.dll"
Type: files; Name: "{app}\ReachFramework.dll"
Type: files; Name: "{app}\System.Xaml.dll"
Type: files; Name: "{app}\UIAutomation*.dll"
Type: files; Name: "{app}\WindowsFormsIntegration.dll"
Type: files; Name: "{app}\System.Windows.Controls.Ribbon.dll"
Type: files; Name: "{app}\System.Windows.Input.Manipulations.dll"
Type: files; Name: "{app}\System.Windows.Presentation.dll"
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TradeFoundry"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\TradeFoundry.ico"
Name: "{autodesktop}\TradeFoundry"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\TradeFoundry.ico"; Tasks: desktopicon

[Registry]
; The tray menu's "Start at sign-in" option writes this value; remove it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "TradeFoundry"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Start TradeFoundry"; Flags: nowait postinstall skipifsilent
; After a silent update started from the tray, start the tray again automatically. It waits
; for Setup to exit before starting the server. (A failed update relaunches it from
; DeinitializeSetup instead.)
Filename: "{app}\{#MyAppExeName}"; Parameters: "--after-update"; Flags: nowait; Check: IsTrayUpdate

[Code]
const
  { The server and the tray helper each hold one of these mutexes while running. }
  RunningMutexes = 'TradeFoundry.Desktop,TradeFoundry.DesktopShell';
  TrayUpdateWaitSeconds = 60;

var
  DataDirectoryPage: TInputDirWizardPage;
  InstallCompleted: Boolean;

{ True when the tray's updater started Setup (it passes /TFUPDATE=1). }
function IsTrayUpdate: Boolean;
begin
  Result := ExpandConstant('{param:TFUPDATE|0}') = '1';
end;

function WaitForAppToExit(TimeoutSeconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  while CheckForMutexes(RunningMutexes) and (Waited < TimeoutSeconds * 10) do
  begin
    Sleep(100);
    Waited := Waited + 1;
  end;
  Result := not CheckForMutexes(RunningMutexes);
end;

{ Setup and uninstall must not replace files while TradeFoundry is running. }
function EnsureAppClosed: Boolean;
begin
  if IsTrayUpdate then
  begin
    { The tray stops the server and exits right after starting Setup. }
    Result := WaitForAppToExit(TrayUpdateWaitSeconds);
    if not Result then
      Log('TradeFoundry is still running; the update was cancelled.');
    Exit;
  end;

  Result := True;
  while CheckForMutexes(RunningMutexes) do
  begin
    if SuppressibleMsgBox(
      'TradeFoundry is running. Right-click its tray icon and choose Quit TradeFoundry, then click OK to continue, or Cancel to exit.',
      mbError, MB_OKCANCEL, IDCANCEL) <> IDOK then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function InitializeSetup: Boolean;
begin
  Result := EnsureAppClosed;
end;

function InitializeUninstall: Boolean;
begin
  Result := EnsureAppClosed;
end;

{ A silent update shows nothing, so if it did not finish, start the tray again and let it
  report the failure in its menu. The tray passes its own path as /TFTRAY=... }
procedure DeinitializeSetup;
var
  TrayPath: string;
  ResultCode: Integer;
begin
  if (not IsTrayUpdate) or InstallCompleted or CheckForMutexes(RunningMutexes) then
    Exit;

  TrayPath := ExpandConstant('{param:TFTRAY|}');
  if (TrayPath = '') or (not FileExists(TrayPath)) then
  begin
    Log('The update did not finish and the tray path is unknown; not restarting TradeFoundry.');
    Exit;
  end;

  Log('The update did not finish; restarting TradeFoundry to report it.');
  Exec(TrayPath, '--after-update --update-failed', ExtractFileDir(TrayPath), SW_SHOWNORMAL, ewNoWait, ResultCode);
end;

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
    'Existing journal data in this directory will be used in place. The installer will not delete or overwrite it.' + #13#10#13#10 +
    'Backups are saved to ' + ExpandConstant('{localappdata}\TradeFoundry\Backups') + ' every day at 02:00. ' +
    'Choose a folder on another drive, encryption and Backblaze B2 in Settings > Backups.',
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
  if CurStep = ssDone then
    InstallCompleted := True;

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

{ Uninstall never deletes journals or backups, so say where they are. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep <> usPostUninstall) or UninstallSilent then
    Exit;
  MsgBox(
    'TradeFoundry was removed. Your journal data and backups were kept:' + #13#10#13#10 +
    '- Journal data: the folder chosen during setup (by default ' + ExpandConstant('{localappdata}\TradeFoundry\Data') + ')' + #13#10 +
    '- Backups: ' + ExpandConstant('{localappdata}\TradeFoundry\Backups') + ', or the folder chosen in Settings > Backups' + #13#10#13#10 +
    'Delete them yourself if you no longer need them. To move to another computer, install TradeFoundry there and use Restore from a backup.',
    mbInformation, MB_OK);
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'DataDirectory', DataDirectoryPage.Values[0]);
end;
