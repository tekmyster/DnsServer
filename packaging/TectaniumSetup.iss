#ifndef PackageDir
  #error PackageDir is required
#endif
#ifndef ReleaseVersion
  #error ReleaseVersion is required
#endif
#ifndef OutputPath
  #error OutputPath is required
#endif
[Setup]
AppId={{55F45C41-7E50-43B4-9D0B-CA1F0334F1A8}
AppName=teQtanium DDI Server
AppVersion={#ReleaseVersion}
AppPublisher=Arqilo
AppPublisherURL=https://github.com/tekmyster/teQtanium-DDI-Server
DefaultDirName={autopf}\teQtanium DDI Server
DefaultGroupName=teQtanium DDI Server
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
OutputDir={#OutputPath}
OutputBaseFilename=teQtanium-{#ReleaseVersion}-windows-x64-setup
SetupIconFile={#PackageDir}\www\favicon.ico
UninstallDisplayIcon={app}\www\favicon.ico
LicenseFile={#PackageDir}\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no
RestartApplications=no
UninstallDisplayName=teQtanium DDI Server
[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Open console"; Filename: "http://127.0.0.1:{param:ConsolePort|5380}/"
Name: "{group}\Project and downloads"; Filename: "https://github.com/tekmyster/teQtanium-DDI-Server/releases"
[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\windows-service.ps1"" -AppDir ""{app}"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveTectaniumService"
[Code]
var ServiceValidated: Boolean;

function GetCustomSetupExitCode(): Integer;
begin
  if not ServiceValidated then Result := 1;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ExitCode: Integer;
begin
  ExtractTemporaryFile('windows-service.ps1');
  if not ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\windows-service.ps1') + '" -AppDir "' + ExpandConstant('{app}') + '" -DnsPort ' + ExpandConstant('{param:DnsPort|53}') + ' -ConsolePort ' + ExpandConstant('{param:ConsolePort|5380}') + ' -Check', '', SW_HIDE, ewWaitUntilTerminated, ExitCode, nil) or (ExitCode <> 0) then
    Result := 'An existing deployment or occupied port prevents installation. Use an isolated host or follow the migration guide.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var ExitCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    ServiceValidated := ExecAndLogOutput(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{app}\windows-service.ps1') + '" -AppDir "' + ExpandConstant('{app}') + '" -DnsPort ' + ExpandConstant('{param:DnsPort|53}') + ' -ConsolePort ' + ExpandConstant('{param:ConsolePort|5380}'), '', SW_HIDE, ewWaitUntilTerminated, ExitCode, nil);
    ServiceValidated := ServiceValidated and (ExitCode = 0);
    if not ServiceValidated then
      RaiseException('The service did not complete installation and administrative validation. Check the setup log before retrying.');
  end;
end;
