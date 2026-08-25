#define AppVersion GetEnv("CANAIRY_VERSION")
#if AppVersion == ""
  #undef AppVersion
  #define AppVersion "0.1.0-dev"
#endif

#define PublishDir GetEnv("CANAIRY_PUBLISH_DIR")
#if PublishDir == ""
  #undef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

#define ReleaseDir GetEnv("CANAIRY_RELEASE_DIR")
#if ReleaseDir == ""
  #undef ReleaseDir
  #define ReleaseDir "..\artifacts\release"
#endif

[Setup]
AppId={{E82D08F8-15A9-4A90-BD37-42E9955EE1B0}
AppName=CanAIRy
AppVersion={#AppVersion}
AppVerName=CanAIRy {#AppVersion}
AppPublisher=Bobby Glidwell
AppPublisherURL=https://github.com/bglidwell/CanAIRy
AppSupportURL=https://github.com/bglidwell/CanAIRy/issues
AppUpdatesURL=https://github.com/bglidwell/CanAIRy/releases
DefaultDirName={localappdata}\Programs\CanAIRy
DefaultGroupName=CanAIRy
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\CanAIRy.exe
OutputDir={#ReleaseDir}
OutputBaseFilename=CanAIRy-{#AppVersion}-win-x64-setup
SetupIconFile=..\app\CanAIRy\Assets\CanAIRy.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CanAIRy"; Filename: "{app}\CanAIRy.exe"
Name: "{autodesktop}\CanAIRy"; Filename: "{app}\CanAIRy.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\CanAIRy.exe"; Description: "Launch CanAIRy"; Flags: nowait postinstall skipifsilent
