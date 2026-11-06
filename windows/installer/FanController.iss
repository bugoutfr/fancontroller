#ifndef AppVersion
  #error AppVersion must be provided by Build-Setup.ps1.
#endif
#ifndef PublishDir
  #error PublishDir must be provided by Build-Setup.ps1.
#endif
#ifndef InstallerOutputDir
  #error InstallerOutputDir must be provided by Build-Setup.ps1.
#endif

[Setup]
AppId={{563F6384-48A5-4A28-AD23-0F30B25F712C}
AppName=FanController
AppVersion={#AppVersion}
AppPublisher=bugoutfr
AppPublisherURL=https://github.com/bugoutfr/fancontroller
DefaultDirName={localappdata}\Programs\FanController
DefaultGroupName=FanController
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir={#InstallerOutputDir}
OutputBaseFilename=FanController-Setup-{#AppVersion}-win-x64
SetupIconFile={#PublishDir}\Assets\FanController.ico
UninstallDisplayIcon={app}\FanController.App.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FanController"; Filename: "{app}\FanController.App.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FanController"; Filename: "{app}\FanController.App.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FanController.App.exe"; Description: "{cm:LaunchProgram,FanController}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  StartupCommand: String;
  ExpectedCommand: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ExpectedCommand := '"' + ExpandConstant('{app}\FanController.App.exe') + '" --startup';
    if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
      'FanController', StartupCommand) then
    begin
      if CompareText(StartupCommand, ExpectedCommand) = 0 then
      begin
        if not RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run',
          'FanController') then
          RaiseException('Could not remove the FanController startup entry.');
      end;
    end;
  end;
end;
