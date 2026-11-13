#ifndef AppVersion
  #define AppVersion "1.2.1"
#endif
#ifndef PayloadDir
  #error Informe /DPayloadDir com a publicação self-contained.
#endif
#ifndef OutputPath
  #define OutputPath "..\dist"
#endif

[Setup]
AppId={{72C43EA9-7CF6-47C4-87BD-C40A2B280E1B}
AppName=DendroLab
AppVersion={#AppVersion}
AppPublisher=leoipp
AppPublisherURL=https://github.com/leoipp/DendroLab
DefaultDirName={localappdata}\Programs\DendroLab
DefaultGroupName=DendroLab
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputPath}
OutputBaseFilename=DendroLab-{#AppVersion}-win-x64-Setup
SetupIconFile=..\src\Dendro.Desktop\Assets\logo.ico
UninstallDisplayIcon={app}\DendroLab-{#AppVersion}.ico
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\Dendro.Desktop\Assets\logo.ico"; DestDir: "{app}"; DestName: "DendroLab-{#AppVersion}.ico"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\DendroLab"; Filename: "{app}\DendroLab.exe"; WorkingDir: "{app}"; IconFilename: "{app}\DendroLab-{#AppVersion}.ico"
Name: "{userdesktop}\DendroLab"; Filename: "{app}\DendroLab.exe"; WorkingDir: "{app}"; IconFilename: "{app}\DendroLab-{#AppVersion}.ico"; Tasks: desktopicon

[Run]
Filename: "{app}\DendroLab.exe"; Description: "Abrir DendroLab"; Flags: nowait postinstall skipifsilent; Check: not IsUpdateInstall
Filename: "{app}\DendroLab.exe"; Flags: nowait; Check: IsUpdateInstall

[Code]
procedure SHChangeNotify(EventId: Integer; Flags: Cardinal; Item1, Item2: Integer);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    SHChangeNotify($08000000, 0, 0, 0);
end;

function IsUpdateInstall: Boolean;
begin
  Result := ExpandConstant('{param:DENDROUPDATE|0}') = '1';
end;
