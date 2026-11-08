#define AppTitle "PCimple Monitor"
#define ServiceName "PCimpleMonitorSensors"
#if Arch == "x64"
#if LowerCase(GetSHA256OfFile(PawnIOSetupPath)) != LowerCase(PawnIOHash)
#error The PawnIO setup file does not match the pinned SHA-256.
#endif
#endif

[Setup]
AppId=PCimpleMonitor
AppName={#AppTitle}
AppVersion={#Version}
AppPublisher={#AppTitle}
DefaultDirName={commonpf}\PCimple Monitor
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
MinVersion=10.0.19045
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed={#Arch}os
#if Arch == "x64"
ArchitecturesInstallIn64BitMode=x64os
#endif
#endif
OutputDir={#OutputRoot}
OutputBaseFilename=PCimpleMonitor-{#Version}-{#Arch}-Setup
SetupIconFile=..\src\PCimpleMonitor\Assets\PCimpleMonitor.ico
UninstallDisplayIcon={app}\PCimpleMonitor.exe
Compression=lzma2/fast
SolidCompression=yes
WizardStyle=modern dynamic
CloseApplications=yes
CloseApplicationsFilter=PCimpleMonitor.exe,PCimpleMonitor.Sensors.exe
RestartApplications=no
SetupLogging=yes
#ifdef SignedRelease
SignTool=pcimple
SignedUninstaller=yes
#endif

[Tasks]
#if Arch == "x64"
Name: "cpusensor"; Description: "Enable CPU temperature sensing with the signed PawnIO driver"
#endif

[Files]
#if Arch == "x64"
Source: "{#PawnIOSetupPath}"; DestDir: "{tmp}"; DestName: "PawnIO_setup.exe"; Flags: dontcopy
#endif
Source: "{#PublishRoot}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\sensors\*"; DestDir: "{app}\Sensors"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#PublishRoot}\notices\*"; DestDir: "{app}\Notices"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{commonprograms}\PCimple Monitor"; Filename: "{app}\PCimpleMonitor.exe"

[Registry]
Root: HKLM; Subkey: "Software\PCimple Monitor"; Flags: uninsdeletekey

[Run]
Filename: "{app}\PCimpleMonitor.exe"; Description: "Open PCimple Monitor"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: CanLaunch

[Code]
const
  ServiceKey = '{#ServiceName}';
#if Arch == "x64"
  PawnIOKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';
  PawnHash = '{#PawnIOHash}';
#endif

type
  TServiceStatus = record
    ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint: LongWord;
  end;

function OpenSCManagerW(MachineName, DatabaseName: Integer; Access: LongWord): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function OpenServiceW(Manager: THandle; Name: String; Access: LongWord): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function QueryServiceStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function ControlService(Service: THandle; Code: LongWord; var Status: TServiceStatus): Boolean;
  external 'ControlService@advapi32.dll stdcall';
function CloseServiceHandle(Service: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

var
  RestartOldService: Boolean;
  ServiceConfigured: Boolean;
  RebootDriver: Boolean;

function CanLaunch: Boolean;
begin
  Result := not RebootDriver;
end;

function Sc(const Params: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
  Log('sc.exe ' + Params + ' returned ' + IntToStr(Result));
end;

function StopService(var WasRunning: Boolean): Boolean;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
  Attempts: Integer;
begin
  WasRunning := False;
  Result := False;
  Manager := OpenSCManagerW(0, 0, 1);
  if Manager = 0 then Exit;
  try
    Service := OpenServiceW(Manager, ServiceKey, $0024);
    if Service = 0 then
    begin
      Result := DLLGetLastError = 1060;  // No previous service.
      Exit;
    end;
    try
      if not QueryServiceStatus(Service, Status) then Exit;
      WasRunning := Status.CurrentState <> 1;
      if WasRunning and (Status.CurrentState <> 3) then
        ControlService(Service, 1, Status);
      for Attempts := 0 to 60 do
      begin
        if not QueryServiceStatus(Service, Status) then Exit;
        if Status.CurrentState = 1 then
        begin
          Result := True;
          Exit;
        end;
        Sleep(250);
      end;
    finally
      CloseServiceHandle(Service);
    end;
  finally
    CloseServiceHandle(Manager);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  WasRunning: Boolean;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{commonpf}\PCimple Monitor')) <> 0 then
  begin
    Result := 'PCimple Monitor must be installed in Program Files because the sensor service needs a protected folder.';
    Exit;
  end;
  if not StopService(WasRunning) then
    Result := 'Setup could not stop the existing sensor service. Close PCimple Monitor and retry.';
  RestartOldService := RestartOldService or WasRunning;
end;

#if Arch == "x64"
function PawnVersionAtLeast220(const Value: String): Boolean;
var
  Version: Int64;
begin
  Result := False;
  if StrToVersion(Value, Version) or StrToVersion(Value + '.0', Version) then
    Result := ComparePackedVersion(Version, PackVersionComponents(2, 2, 0, 0)) >= 0;
end;

function PawnInstalled: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM64, PawnIOKey, 'DisplayVersion', Version) and PawnVersionAtLeast220(Version))
         or (RegQueryStringValue(HKLM32, PawnIOKey, 'DisplayVersion', Version) and PawnVersionAtLeast220(Version));
end;

function InstallPawnIO(var Failure: String): String;
var
  Path, Vendor: String;
  Code: Integer;
begin
  Result := 'skipped';
  if PawnInstalled then
  begin
    Result := 'ready';
    Exit;
  end;
  if not WizardIsTaskSelected('cpusensor') then Exit;
  if not RegQueryStringValue(HKLM, 'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'VendorIdentifier', Vendor)
     or ((Vendor <> 'GenuineIntel') and (Vendor <> 'AuthenticAMD')) then Exit;

  Result := 'payload-error';
  try
    WizardForm.StatusLabel.Caption := 'Verifying the bundled PawnIO setup...';
    ExtractTemporaryFile('PawnIO_setup.exe');
    Path := ExpandConstant('{tmp}\PawnIO_setup.exe');
    if CompareText(GetSHA256OfFile(Path), PawnHash) <> 0 then
    begin
      Failure := 'The bundled PawnIO setup failed its integrity check. Get a fresh copy of the PCimple Monitor installer.';
      Exit;
    end;
    Result := 'install-error';
    WizardForm.StatusLabel.Caption := 'Enabling the CPU temperature sensor...';
    if not Exec(Path, '-install -silent', ExpandConstant('{tmp}'), SW_HIDE, ewWaitUntilTerminated, Code) then
    begin
      Failure := 'Windows could not launch PawnIO setup (error ' + IntToStr(Code) + ').';
      Exit;
    end;
    Log('PawnIO setup returned ' + IntToStr(Code));
    if Code = 3010 then
      Result := 'reboot'
    else if (Code = 0) and PawnInstalled then
      Result := 'ready'
    else if Code = 0 then
      Failure := 'PawnIO setup finished, but Windows did not register the expected driver version.'
    else
      Failure := 'PawnIO setup returned error ' + IntToStr(Code) + ' (' + SysErrorMessage(Code) + ').';
  except
    Log(GetExceptionMessage);
    Failure := 'Setup could not prepare the bundled PawnIO driver. See the setup log for details.';
  end;
end;
#endif

procedure CurStepChanged(Step: TSetupStep);
var
  Status, Params, Failure: String;
  Code: Integer;
begin
  if Step <> ssPostInstall then Exit;

  Params := ServiceKey + ' binPath= "\"' + ExpandConstant('{app}\Sensors\PCimpleMonitor.Sensors.exe') +
    '\"" start= auto DisplayName= "PCimple Monitor Sensors"';
  Code := Sc('create ' + Params);
  if Code = 1073 then Code := Sc('config ' + Params);
  if Code <> 0 then
    RaiseException('Setup could not register the PCimple Monitor sensor service.');

#if Arch == "x64"
  Status := InstallPawnIO(Failure);
#else
  Status := 'unsupported';
#endif
  RegWriteStringValue(HKLM, 'Software\PCimple Monitor', 'PawnIOStatus', Status);
  if Failure = '' then
    RegDeleteValue(HKLM, 'Software\PCimple Monitor', 'PawnIOError')
  else
  begin
    Log(Failure);
    RegWriteStringValue(HKLM, 'Software\PCimple Monitor', 'PawnIOError', Failure);
    SuppressibleMsgBox('PCimple Monitor was installed. CPU temperature is unavailable.' + #13#10#13#10 + Failure,
      mbInformation, MB_OK, IDOK);
  end;
  RebootDriver := Status = 'reboot';

  ServiceConfigured := True;
  Code := Sc('start ' + ServiceKey);
  if (Code <> 0) and (Code <> 1056) then
    SuppressibleMsgBox('The sensor service did not start. Restart Windows or rerun setup to recover CPU and drive readings.', mbError, MB_OK, IDOK);
end;

function NeedRestart: Boolean;
begin
  Result := RebootDriver;
end;

procedure DeinitializeSetup;
begin
  if RestartOldService and not ServiceConfigured then
    Sc('start ' + ServiceKey);
end;

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  WasRunning: Boolean;
  Code: Integer;
begin
  if Step <> usUninstall then Exit;
  if not StopService(WasRunning) then
    RaiseException('Uninstall could not stop the PCimple Monitor sensor service.');
  Code := Sc('delete ' + ServiceKey);
  if (Code <> 0) and (Code <> 1060) and (Code <> 1072) then
    RaiseException('Uninstall could not remove the PCimple Monitor sensor service.');
end;
