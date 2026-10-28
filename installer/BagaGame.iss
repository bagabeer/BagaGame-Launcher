; BagaGame — Inno Setup script.
; Build:  publish the app first, then compile this script with ISCC:
;   dotnet publish BagaGame.csproj -c Release -o .artifacts\publish
;   iscc installer\BagaGame.iss
;
; The payload is framework-dependent (net8.0-windows), so the target machine
; needs the .NET 8 Desktop Runtime; Setup offers the download page when it is
; missing.

#define AppName "BagaGame"
#define AppVersion "0.6.0"
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

[Code]
// BagaGame is published framework-dependent, so the .NET 8 Desktop Runtime
// (Microsoft.WindowsDesktop.App 8.x) has to be present before Setup copies the
// files; otherwise the installed app would not start at all.
function HasDotNet8DesktopRuntime: Boolean;
var
  FindRec: TFindRec;
  SearchPath: String;
begin
  Result := False;
  SearchPath := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*');

  if FindFirst(SearchPath, FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

  if HasDotNet8DesktopRuntime then
  begin
    Exit;
  end;

  if MsgBox(
       'Для работы BagaGame нужен .NET 8 Desktop Runtime, но он не найден на этом компьютере.'
       + #13#10 + #13#10
       + 'Открыть страницу загрузки? После установки запустите Setup снова.',
       mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0',
              '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;

  Result := False;
end;