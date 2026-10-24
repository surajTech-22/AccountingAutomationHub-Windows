; GitHub signed MainServer/standalone bootstrap installer.
; User data under LocalAppData is not part of the payload or uninstall list.
; LAN workstation role installations are a separate product.
#define AAHVersion GetEnv("AAH_RELEASE_VERSION")
#define AAHPublish GetEnv("AAH_RELEASE_PUBLISH")
#if AAHVersion == ""
  #error "AAH_RELEASE_VERSION must be specified by signed release workflow"
#endif
#if AAHPublish == ""
  #error "AAH_RELEASE_PUBLISH must be specified by signed release workflow"
#endif

[Setup]
AppId={{D8E4758C-09F1-45BF-831A-59C22B79DC04}
AppName=Accounting Automation Hub
AppVersion={#AAHVersion}
AppPublisher=AAH
DefaultDirName={localappdata}\Programs\AccountingAutomationHub
DefaultGroupName=Accounting Automation Hub
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputBaseFilename=AAH_Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
DisableDirPage=no
UninstallDisplayIcon={app}\AccountingAutomationHub.exe
SetupLogging=yes

[Files]
Source: "{#AAHPublish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Accounting Automation Hub"; Filename: "{app}\AccountingAutomationHub.exe"

[Run]
Filename: "{app}\AccountingAutomationHub.exe"; Description: "Start Accounting Automation Hub"; Flags: nowait postinstall skipifsilent
