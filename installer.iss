; PKG Sender installer — per-user, no admin needed. Mirrors pkg-viewer/installer.iss.
#define AppVer "1.2.2"

[Setup]
AppName=PKG Sender
AppVersion={#AppVer}
AppPublisher=Loopayeh
DefaultDirName={autopf}\PKG Sender
DefaultGroupName=PKG Sender
OutputDir=D:\OpenCode\pkg-sender
OutputBaseFilename=PkgSender-Setup-{#AppVer}
PrivilegesRequired=lowest
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayName=PKG Sender
WizardStyle=modern
SetupIconFile=D:\OpenCode\pkg-sender\library\Assets\logo.ico

[Files]
Source: "D:\OpenCode\pkg-sender\dist\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\PKG Sender"; Filename: "{app}\PkgSender.exe"
Name: "{autodesktop}\PKG Sender"; Filename: "{app}\PkgSender.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop icon"; Flags: checkedonce

[Run]
; inbound rules so the console can pull PKGs from the PC file server
; (TCP 9898) and the app can hear receiver beacons (UDP 12801).
; missing rules are the most common cause of "push ok but nothing happens".
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""PKG Sender"" dir=in action=allow protocol=TCP localport=9898 program=""{app}\PkgSender.exe"""; Flags: runhidden; StatusMsg: "Adding firewall rule…"
Filename: "netsh.exe"; Parameters: "advfirewall firewall add rule name=""PKG Sender beacon"" dir=in action=allow protocol=UDP localport=12801 program=""{app}\PkgSender.exe"""; Flags: runhidden
Filename: "{app}\PkgSender.exe"; Parameters: "--first-install"; Description: "Launch PKG Sender"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PKG Sender"""; Flags: runhidden
Filename: "netsh.exe"; Parameters: "advfirewall firewall delete rule name=""PKG Sender beacon"""; Flags: runhidden

[Registry]
; show in Settings -> Default apps (per-user, no admin)
Root: HKCU; Subkey: "Software\Loopayeh\PKGSender\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "PKG Sender"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Loopayeh\PKGSender\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "PKG install over LAN"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "PKG Sender"; ValueData: "Software\Loopayeh\PKGSender\Capabilities"; Flags: uninsdeletevalue

[Code]
procedure SHChangeNotify(wEventID: Integer; uFlags: Cardinal; dwItem1, dwItem2: Cardinal);
  external 'SHChangeNotify@shell32.dll stdcall';

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    // auto-close running app so files are not locked (silent, no prompt)
    try
      Exec('taskkill.exe', '/F /IM PkgSender.exe', '', SW_HIDE,
           ewWaitUntilTerminated, ResultCode);
    except
    end;
  end;
  if CurStep = ssPostInstall then
  begin
    // refresh explorer icon cache so new icons show immediately
    try
      SHChangeNotify($08000000, 0, 0, 0);
    except
    end;
  end;
end;
