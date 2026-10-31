#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif
#ifndef MyAppSource
  #define MyAppSource "publish"
#endif
[Setup]
AppId={{F59428C8-9792-4774-91DE-97E28CBB5472}
AppName=Чисто
AppVersion={#MyAppVersion}
AppPublisher=saymonseo
AppPublisherURL=https://github.com/saymonseo/chisto
AppUpdatesURL=https://github.com/saymonseo/chisto/releases
DefaultDirName={code:DefaultInstallDir}
DefaultGroupName=Чисто
DisableProgramGroupPage=yes
DisableDirPage=no
UsePreviousAppDir=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\Chisto.exe
SetupIconFile=Assets\app.ico
OutputDir=release
OutputBaseFilename=Chisto-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}
[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"
[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Ярлыки:"; Flags: unchecked
[Files]
Source: "{#MyAppSource}\Chisto.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyAppSource}\update-source.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion
[Icons]
Name: "{userprograms}\Чисто\Чисто"; Filename: "{app}\Chisto.exe"
Name: "{userdesktop}\Чисто"; Filename: "{app}\Chisto.exe"; Tasks: desktopicon
[Run]
Filename: "{app}\Chisto.exe"; Description: "Запустить Чисто"; Flags: nowait postinstall skipifsilent
[Code]
function DefaultInstallDir(Param: String): String;
begin
  if DirExists('D:\') then Result := 'D:\Programs\Chisto'
  else Result := ExpandConstant('{localappdata}\Programs\Chisto');
end;

function HasDesktopRuntime: Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if not RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions) then
    RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Versions);
  for I := 0 to GetArrayLength(Versions) - 1 do
    if Pos('10.', Versions[I]) = 1 then Result := True;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not HasDesktopRuntime then begin
    MsgBox('Для работы нужен .NET Desktop Runtime 10 (x64). Откроется официальная страница Microsoft. После установки Runtime запустите этот установщик снова.', mbInformation, MB_OK);
    ShellExec('open', 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;
