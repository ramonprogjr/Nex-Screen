; NexScreen installer (Inno Setup 6).
;
; Build with tools\build-installer.ps1, which stages the payload and runs ISCC with:
;   /DAppVersion=0.2.0       version shown to the user (from the git tag)
;   /DFileVersion=0.2.0.0    numeric version for the setup's version resource
;   /DStageDir=<dir>         companion\ (dotnet publish), platform-tools\ (adb), vdd\ (VDD driver),
;                            nexscreen-vdd-ctl.exe
;   /DOutputDir=<dir>        where NexScreen-Setup-v<AppVersion>.exe is written
;
; What it installs (admin, per machine):
;   {app}                          the companion (self-contained), nexscreen-vdd-ctl.exe, licenses
;   {app}\platform-tools           adb; also appended to the system PATH so the companion finds it when it is
;                                  started from the HKCU Run value (which has no --adb, see StartupRegistration.cs)
;   {app}\vdd                      the signed Virtual Display Driver package, installed on the Root\MttVDD device
;   C:\VirtualDisplayDriver        vdd_settings.xml (the path the driver reads); an existing different file is
;                                  backed up and restored on uninstall
;
; Command-line options (besides Inno's /SILENT, /VERYSILENT, /LOG=...):
;   setup:       /AUTOSTART  with /SILENT or /VERYSILENT, also write the "Start with Windows" Run value
;   uninstaller: /KEEPVDD    keep the virtual monitor (device, driver package and vdd_settings.xml)

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef FileVersion
  #define FileVersion "0.0.0.0"
#endif
#define RepoRoot AddBackslash(SourcePath) + "..\.."
#ifndef StageDir
  #define StageDir RepoRoot + "\out\installer\stage"
#endif
#ifndef OutputDir
  #define OutputDir RepoRoot + "\out\installer"
#endif

#define AppExe "NexScreen.Companion.exe"
#define VddHardwareId "Root\MttVDD"
#define VddSettingsDir "C:\VirtualDisplayDriver"

[Setup]
AppId={{C1DC1EFF-E67A-473D-A1EF-6304C039DDA1}
AppName=NexScreen
AppVersion={#AppVersion}
AppVerName=NexScreen {#AppVersion}
AppPublisher=Ramon Pereira
AppPublisherURL=https://github.com/ramonprogjr/Nex-Screen
AppSupportURL=https://github.com/ramonprogjr/Nex-Screen/issues
AppUpdatesURL=https://github.com/ramonprogjr/Nex-Screen/releases
AppCopyright=Copyright (c) Ramon Pereira
VersionInfoVersion={#FileVersion}
VersionInfoProductVersion={#FileVersion}
VersionInfoDescription=NexScreen Setup
DefaultDirName={autopf}\NexScreen
; Fixed folder (Program Files\NexScreen); the PATH entry and the Run value point into it
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; The companion targets Windows 10 2004 (10.0.19041) and later
MinVersion=10.0.19041
LicenseFile={#RepoRoot}\LICENSE
SetupIconFile={#RepoRoot}\windows\companion\src\NexScreen.Companion\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName=NexScreen
OutputDir={#OutputDir}
OutputBaseFilename=NexScreen-Setup-v{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ChangesEnvironment=yes
; Running companion/adb processes are stopped by [Code] (only the ones under {app})
CloseApplications=no
SetupLogging=yes
ShowLanguageDialog=auto
LanguageDetectionMethod=uilanguage

[Languages]
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
ptbr.LaunchNow=Iniciar o NexScreen agora
en.LaunchNow=Start NexScreen now
ptbr.StartWithWindows=Iniciar com o Windows
en.StartWithWindows=Start with Windows
ptbr.ShortcutComment=Usa o tablet como monitor estendido
en.ShortcutComment=Use your tablet as an extended monitor
ptbr.StoppingApp=Fechando o NexScreen...
en.StoppingApp=Closing NexScreen...
ptbr.InstallingMonitor=Instalando o monitor virtual (Virtual Display Driver)...
en.InstallingMonitor=Installing the virtual monitor (Virtual Display Driver)...
ptbr.MonitorFailed=Não foi possível instalar o monitor virtual (código %1).%n%nO NexScreen foi instalado, mas sem o monitor virtual o tablet não recebe imagem. Detalhes em:%n%2
en.MonitorFailed=The virtual monitor could not be installed (code %1).%n%nNexScreen was installed, but without the virtual monitor the tablet gets no image. Details in:%n%2
ptbr.RemoveMonitorQuestion=Remover também o monitor virtual (Virtual Display Driver)?%n%nResponda "Não" se você usa esse monitor virtual com outro programa.
en.RemoveMonitorQuestion=Also remove the virtual monitor (Virtual Display Driver)?%n%nAnswer "No" if you use this virtual monitor with another program.
ptbr.RemoveMonitorFailed=Não foi possível remover o monitor virtual (código %1). Detalhes em:%n%2
en.RemoveMonitorFailed=The virtual monitor could not be removed (code %1). Details in:%n%2

[Files]
Source: "{#StageDir}\companion\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#StageDir}\nexscreen-vdd-ctl.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#StageDir}\platform-tools\*"; DestDir: "{app}\platform-tools"; Flags: ignoreversion
Source: "{#StageDir}\vdd\*"; DestDir: "{app}\vdd"; Flags: ignoreversion
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\driver\virtual-display\THIRD_PARTY.md"; DestDir: "{app}"; DestName: "driver-THIRD_PARTY.md"; Flags: ignoreversion
; Removed (or kept) by [Code] depending on the "remove the virtual monitor" answer
Source: "vdd_settings.xml"; DestDir: "{#VddSettingsDir}"; Flags: ignoreversion uninsneveruninstall; BeforeInstall: BackupVddSettings

[Icons]
Name: "{autoprograms}\NexScreen"; Filename: "{app}\{#AppExe}"; Parameters: "--tray --adb ""{app}\platform-tools\adb.exe"""; WorkingDir: "{app}"; Comment: "{cm:ShortcutComment}"

[Registry]
; adb on the system PATH (see the header). Removed by [Code] on uninstall.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\platform-tools"; Check: NeedsAddPath

[Run]
; Finished page. runasoriginaluser: the companion runs, and the Run value is written, as the user who started Setup.
Filename: "{app}\{#AppExe}"; Parameters: "--tray --adb ""{app}\platform-tools\adb.exe"""; WorkingDir: "{app}"; Description: "{cm:LaunchNow}"; Flags: postinstall nowait skipifsilent runasoriginaluser
Filename: "{sys}\reg.exe"; Parameters: "{code:RunValueRegArgs}"; Description: "{cm:StartWithWindows}"; Flags: postinstall unchecked skipifsilent runhidden runasoriginaluser
; Silent installs: /AUTOSTART
Filename: "{sys}\reg.exe"; Parameters: "{code:RunValueRegArgs}"; Flags: runhidden runasoriginaluser; Check: AutostartRequested

[UninstallDelete]
; Only what Setup itself created; never a recursive delete of {app} (/DIR= can point it anywhere)
Type: files; Name: "{app}\install-vdd.log"
Type: dirifempty; Name: "{app}\platform-tools"
Type: dirifempty; Name: "{app}\vdd"
Type: dirifempty; Name: "{app}"

[Code]
const
  VddHardwareId = '{#VddHardwareId}';
  VddSettingsDir = '{#VddSettingsDir}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValueName = 'NexScreen';
  AppSettingsKey = 'Software\NexScreen';
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  ExitRebootRequired = 3;

var
  RestartNeeded: Boolean;
  RemoveVdd: Boolean;

function HasCommandLineSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then
    begin
      Result := True;
      Exit;
    end;
end;

function AppExePath: String;
begin
  Result := ExpandConstant('{app}\{#AppExe}');
end;

function AdbDir: String;
begin
  Result := ExpandConstant('{app}\platform-tools');
end;

{ ---- "Start with Windows": same value format as Tray\StartupRegistration.cs: "<exe>" --tray ---- }

function RunValueRegArgs(Param: String): String;
begin
  Result := 'add "HKCU\' + RunKey + '" /v ' + RunValueName + ' /t REG_SZ /f /d "\"' + AppExePath + '\" --tray"';
end;

function AutostartRequested: Boolean;
begin
  Result := WizardSilent and HasCommandLineSwitch('/AUTOSTART');
end;

{ ---- PATH ---- }

function PathContains(const PathValue, Dir: String): Boolean;
begin
  Result := Pos(';' + Uppercase(RemoveBackslashUnlessRoot(Dir)) + ';', ';' + Uppercase(PathValue) + ';') > 0;
end;

function NeedsAddPath: Boolean;
var
  Current: String;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Current) then
    Current := '';
  Result := not PathContains(Current, AdbDir);
end;

procedure RemoveFromPath(const Dir: String);
var
  Current, Updated, Entry: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvironmentKey, 'Path', Current) then
    Exit;
  Updated := '';
  while Current <> '' do
  begin
    P := Pos(';', Current);
    if P = 0 then
    begin
      Entry := Current;
      Current := '';
    end
    else
    begin
      Entry := Copy(Current, 1, P - 1);
      Delete(Current, 1, P);
    end;
    if (Entry <> '') and (CompareText(RemoveBackslashUnlessRoot(Entry), RemoveBackslashUnlessRoot(Dir)) <> 0) then
    begin
      if Updated <> '' then
        Updated := Updated + ';';
      Updated := Updated + Entry;
    end;
  end;
  if RegWriteExpandStringValue(HKLM, EnvironmentKey, 'Path', Updated) then
    Log('Removed ' + Dir + ' from the system PATH')
  else
    Log('Could not update the system PATH');
end;

{ ---- Processes ---- }

{ Stops the processes named ExeName whose executable is under Dir (never another copy elsewhere) }
procedure StopProcessesUnder(const ExeName, Dir: String);
var
  Locator, Service, Processes, Process: Variant;
  I, ResultCode: Integer;
  ExePath, Prefix: String;
begin
  Prefix := Uppercase(AddBackslash(Dir));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('.', 'root\CIMV2');
    Processes := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''' + ExeName + '''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      if VarIsNull(Process.ExecutablePath) then
        ExePath := ''
      else
        ExePath := Process.ExecutablePath;
      if (ExePath <> '') and (Pos(Prefix, Uppercase(ExePath)) = 1) then
      begin
        Log('Stopping ' + ExePath + ' (pid ' + IntToStr(Process.ProcessId) + ')');
        Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /PID ' + IntToStr(Process.ProcessId), '', SW_HIDE,
          ewWaitUntilTerminated, ResultCode);
      end;
    end;
  except
    Log('Could not enumerate ' + ExeName + ' processes: ' + GetExceptionMessage);
  end;
end;

procedure StopNexScreen;
var
  ResultCode: Integer;
begin
  StopProcessesUnder('{#AppExe}', ExpandConstant('{app}'));
  { The adb server started by the companion keeps adb.exe locked }
  if FileExists(AdbDir + '\adb.exe') then
    Exec(AdbDir + '\adb.exe', 'kill-server', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  StopProcessesUnder('adb.exe', AdbDir);
end;

{ ---- nexscreen-vdd-ctl ---- }

procedure LogFile(const FileName: String);
var
  Lines: TArrayOfString;
  I: Integer;
begin
  if LoadStringsFromFile(FileName, Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
      Log('  ' + Lines[I]);
end;

{ Runs nexscreen-vdd-ctl with Args, output to LogPath; returns its exit code (-1 if it could not start) }
function RunVddCtl(const Args, LogPath: String): Integer;
var
  Ctl: String;
begin
  Ctl := ExpandConstant('{app}\nexscreen-vdd-ctl.exe');
  Log('nexscreen-vdd-ctl ' + Args);
  if not Exec(ExpandConstant('{cmd}'), '/C ""' + Ctl + '" ' + Args + ' > "' + LogPath + '" 2>&1"', '', SW_HIDE,
    ewWaitUntilTerminated, Result) then
    Result := -1;
  Log('nexscreen-vdd-ctl exit code ' + IntToStr(Result));
  LogFile(LogPath);
end;

procedure InstallVdd;
var
  LogPath: String;
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('InstallingMonitor');
  LogPath := ExpandConstant('{app}\install-vdd.log');
  ResultCode := RunVddCtl('install "' + ExpandConstant('{app}\vdd\MttVDD.inf') + '" --hwid ' + VddHardwareId, LogPath);
  if ResultCode = ExitRebootRequired then
    RestartNeeded := True
  else if ResultCode <> 0 then
    SuppressibleMsgBox(FmtMessage(CustomMessage('MonitorFailed'), [IntToStr(ResultCode), LogPath]), mbError, MB_OK, IDOK);
end;

{ ---- vdd_settings.xml ---- }

function VddSettingsFile: String;
begin
  Result := VddSettingsDir + '\vdd_settings.xml';
end;

function VddSettingsBackup: String;
begin
  Result := VddSettingsFile + '.nexscreen-backup';
end;

{ Before vdd_settings.xml is written: keep a different existing file (the user's or another tool's) }
procedure BackupVddSettings;
var
  Ours, Backup: String;
begin
  if not FileExists(VddSettingsFile) then
    Exit;
  ExtractTemporaryFile('vdd_settings.xml');
  Ours := ExpandConstant('{tmp}\vdd_settings.xml');
  if GetSHA256OfFile(VddSettingsFile) = GetSHA256OfFile(Ours) then
    Exit;
  { The first backup holds the file from before NexScreen and is the one restored on uninstall }
  Backup := VddSettingsBackup;
  if FileExists(Backup) then
    Backup := VddSettingsFile + '.nexscreen-backup-' + GetDateTimeString('yyyymmddhhnnss', #0, #0);
  if CopyFile(VddSettingsFile, Backup, False) then
    Log('Backed up ' + VddSettingsFile + ' to ' + Backup)
  else
    Log('Could not back up ' + VddSettingsFile);
end;

procedure RemoveVddSettings;
begin
  if FileExists(VddSettingsFile) then
    DeleteFile(VddSettingsFile);
  if FileExists(VddSettingsBackup) then
  begin
    if RenameFile(VddSettingsBackup, VddSettingsFile) then
      Log('Restored the previous ' + VddSettingsFile);
  end;
  { Only if empty: the user may keep other VDD files there }
  RemoveDir(VddSettingsDir);
end;

{ ---- Per-user registry (HKCU and every loaded user hive, so it also works if Setup ran as another admin) ---- }

procedure CleanUserHive(RootKey: Integer; const Prefix: String);
var
  Value: String;
begin
  if RegQueryStringValue(RootKey, Prefix + RunKey, RunValueName, Value) and
     (Pos(Uppercase(AppExePath), Uppercase(Value)) > 0) then
  begin
    RegDeleteValue(RootKey, Prefix + RunKey, RunValueName);
    Log('Removed the Run value ' + RunValueName + ' (' + Value + ')');
  end;
  if RegKeyExists(RootKey, Prefix + AppSettingsKey) then
  begin
    RegDeleteKeyIncludingSubkeys(RootKey, Prefix + AppSettingsKey);
    Log('Removed ' + Prefix + AppSettingsKey);
  end;
end;

procedure CleanUserRegistry;
var
  Users: TArrayOfString;
  I: Integer;
begin
  CleanUserHive(HKCU, '');
  if RegGetSubkeyNames(HKEY_USERS, '', Users) then
    for I := 0 to GetArrayLength(Users) - 1 do
      if Pos('_CLASSES', Uppercase(Users[I])) = 0 then
        CleanUserHive(HKEY_USERS, Users[I] + '\');
end;

{ ---- Setup events ---- }

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  WizardForm.StatusLabel.Caption := CustomMessage('StoppingApp');
  StopNexScreen;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    InstallVdd;
end;

function NeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;

{ ---- Uninstall events ---- }

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LogPath: String;
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Asked here, after Windows' "remove NexScreen?" confirmation. Default (and silent): remove it. }
    RemoveVdd := not HasCommandLineSwitch('/KEEPVDD');
    if RemoveVdd and not UninstallSilent then
      RemoveVdd := MsgBox(CustomMessage('RemoveMonitorQuestion'), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDYES;

    StopNexScreen;
    if RemoveVdd then
    begin
      LogPath := AddBackslash(GetTempDir) + 'NexScreen-uninstall-vdd.log';
      ResultCode := RunVddCtl('uninstall --hwid ' + VddHardwareId, LogPath);
      if ResultCode = ExitRebootRequired then
        RestartNeeded := True
      else if ResultCode <> 0 then
        SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveMonitorFailed'), [IntToStr(ResultCode), LogPath]), mbError, MB_OK, IDOK);
      RemoveVddSettings;
    end
    else
      Log('Keeping the virtual monitor');
    CleanUserRegistry;
  end
  else if CurUninstallStep = usPostUninstall then
    RemoveFromPath(AdbDir);
end;

function UninstallNeedRestart: Boolean;
begin
  Result := RestartNeeded;
end;
