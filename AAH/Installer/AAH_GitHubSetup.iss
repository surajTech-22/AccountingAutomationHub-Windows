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
UsePreviousAppDir=yes
DisableDirPage=yes
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
UninstallDisplayIcon={app}\AccountingAutomationHub.exe
SetupLogging=yes

[Files]
Source: "{#AAHPublish}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Accounting Automation Hub"; Filename: "{app}\AccountingAutomationHub.exe"

[Run]
Filename: "{app}\AccountingAutomationHub.exe"; Description: "Start Accounting Automation Hub"; Flags: nowait postinstall skipifsilent

; Avoid the specific regression seen in Installed Apps: one 0.99GB legacy
; install plus a new 335MB GitHub install. Different AppIds create TWO
; uninstall registrations, even when copying files into the same folder.
; Until the actual legacy registration is migrated, fail closed instead of
; silently creating a second AAH installation and potentially separating
; its accounting data and Tally links.
[Code]
function HasOtherAAHInstallation(const RootKey: Integer): Boolean;
var
  Subkeys: TArrayOfString;
  I: Integer;
  KeyName: String;
  Display: String;
  BaseKey: String;
begin
  Result := False;
  BaseKey := 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  if not RegGetSubkeyNames(RootKey, BaseKey, Subkeys) then Exit;
  for I := 0 to GetArrayLength(Subkeys) - 1 do
  begin
    KeyName := Subkeys[I];
    if Pos('D8E4758C-09F1-45BF-831A-59C22B79DC04', UpperCase(KeyName)) <> 0 then
      Continue;
    if RegQueryStringValue(RootKey, BaseKey + '\' + KeyName, 'DisplayName', Display) then
    begin
      if (Pos('accounting automation h', LowerCase(Display)) = 1) or
         (LowerCase(Display) = 'aah') or
         (Pos('aah ', LowerCase(Display)) = 1) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := not (
    HasOtherAAHInstallation(HKCU) or
    HasOtherAAHInstallation(HKLM32) or
    HasOtherAAHInstallation(HKLM64)
  );
  if not Result then
    MsgBox(
      'Another Accounting Automation Hub installation is registered with a different installer identity.' + #13#10 + #13#10 +
      'Setup will not create another installation. Your existing AAH and accounting data have NOT been changed.' + #13#10 +
      'Run the read-only ci/Diagnose-AAHInstallations.ps1 audit and migrate the original installation before reinstalling.',
      mbCriticalError, MB_OK);
end;
