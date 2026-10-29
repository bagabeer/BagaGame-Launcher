; BagaGame — Inno Setup script.
; Build:  publish the app first, then compile this script with ISCC:
;   dotnet publish BagaGame.csproj -c Release -o .artifacts\publish
;   iscc installer\BagaGame.iss
;
; The payload targets .NET Framework 4.7.2, which ships with Windows 10 1803+
; and Windows 11, so Setup has no runtime prerequisite to check or install.

#define AppName "BagaGame"
#define AppVersion "0.7.0"
#define AppPublisher "BagaGame"
#define AppExeName "BagaGame.exe"
#define PublishDir "..\.artifacts\publish"
#define ReadmeFile "..\README.md"

[Setup]
AppId={{9F4C7E2A-8B61-4C3D-A7E5-1D6F2B8C4A93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Setup
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern
DisableWelcomePage=no
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
SetupIconFile=..\Assets\bagagame.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ReadmeFile}"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
