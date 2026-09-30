; Inno Setup installer for SpiffoCON, two editions from the same script:
;   Light: framework-dependent build, needs the .NET 10 Desktop Runtime (small setup)
;   Full:  self-contained build, .NET runtime included (no prerequisites)
; Build with build.ps1, which publishes to ..\publish\<edition> and passes
; /DFlavor=Light|Full and /DAppVersion=<version from SpiffoCON.csproj>.

#ifndef Flavor
  #define Flavor "Light"
#endif
#if Flavor != "Light" && Flavor != "Full"
  #error Flavor must be Light or Full
#endif
#ifndef AppVersion
  #error AppVersion is required (pass /DAppVersion=x.y.z)
#endif

#define AppName "SpiffoCON"
#define AppExe "SpiffoCON.exe"
#define SourceDir AddBackslash(SourcePath) + "..\publish\" + LowerCase(Flavor)

[Setup]
; One AppId for both editions: Light and Full replace each other
AppId={{E1FBE2AD-C111-415A-A9DB-A11B483B6ACD}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion} ({#Flavor})
AppPublisher=Phate
AppPublisherURL=https://github.com/MarcoTrombetta/SpiffoCON
AppSupportURL=https://github.com/MarcoTrombetta/SpiffoCON/issues
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Remote admin console for Project Zomboid servers
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin rights needed); all users (with UAC) can be chosen
; in the first dialog.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=Output
OutputBaseFilename=SpiffoCON-Setup-{#AppVersion}-{#Flavor}
SetupIconFile=..\src\SpiffoCON\Assets\spiffocon.ico
LicenseFile=..\LICENSE
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} ({#Flavor})
WizardStyle=modern dynamic
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
; SpiffoCON's own update opens it again ([Run], IsAppUpdate): Restart Manager must not start a second one
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
RuntimeMissing=SpiffoCON requires the .NET 10 Desktop Runtime (x64), which does not appear to be installed.%n%nYes = open the download page and close setup%nNo = install anyway%nCancel = close setup%n%nAlternatively use the Full edition, which includes the runtime.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; the program, plus the bridge mod in "bridge" (used by the Bridge tab's "Prepare Workshop upload")
Source: "{#SourceDir}\*"; Excludes: "*.pdb"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Started by SpiffoCON's own update (silent, /UPDATE=1): open it again when done, as the user (not elevated)
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsAppUpdate

[Code]
{ SpiffoCON runs its own update silently with /UPDATE=1, and is opened again afterwards }
function IsAppUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

{ Switching edition (Full <-> Light) or upgrading: remove the previous program files so no stale
  runtime or library DLLs are left behind, and the bridge folder so no old mod files stay in it.
  The user's data is not in the program folder (profile in %APPDATA%\SpiffoCON, caches and
  sandbox backups in %LOCALAPPDATA%\SpiffoCON) and is never touched. }
procedure CleanProgramFolder;
var
  App, Name: String;
  FindRec: TFindRec;
begin
  App := ExpandConstant('{app}\');
  { Only a folder this setup installed before, and only one named for it: a folder chosen by
    hand (say D:\Tools) may hold other programs' files }
  if (WizardForm.PrevAppDir = '') or
     (CompareText(AddBackslash(WizardForm.PrevAppDir), App) <> 0) or
     not FileExists(App + '{#AppExe}') then
    Exit;
  { The bridge mod's own folder is always ours: no old mod files may be uploaded again }
  DelTree(App + 'bridge\SpiffoCONBridge', True, True, True);
  { Loose files only in a folder named for SpiffoCON }
  if CompareText(ExtractFileName(RemoveBackslash(App)), 'SpiffoCON') <> 0 then
    Exit;
  if FindFirst(App + '*', FindRec) then
  begin
    try
      repeat
        Name := FindRec.Name;
        { program files only: runtime and app DLLs, exes, their .json configs, the license }
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) = 0) and
           (CompareText(Copy(Name, 1, 5), 'unins') <> 0) and
           ((CompareText(ExtractFileExt(Name), '.dll') = 0) or (CompareText(ExtractFileExt(Name), '.exe') = 0) or
            (CompareText(ExtractFileExt(Name), '.json') = 0) or (CompareText(ExtractFileExt(Name), '.pdb') = 0) or
            (CompareText(Name, 'LICENSE.txt') = 0)) then
          DeleteFile(App + Name);
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

const
  EVENT_MODIFY_STATE = $0002;
  SYNCHRONIZE = $00100000;

function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Event: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function OpenProcess(DesiredAccess: DWORD; InheritHandle: BOOL; ProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

{ Started by SpiffoCON's update with /WAITPID=<its process>: now past the administrator prompt, ask it
  to close (the event UpdateCheck.ReadyEventName names) and wait until it is gone, before Restart Manager
  looks for it: closed by Restart Manager, WPF crashes on its way out. Still open after a minute, Restart
  Manager closes it after all. }
procedure CloseUpdatedApp;
var
  Pid: Integer;
  Handle: THandle;
begin
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid <= 0 then
    Exit;
  Handle := OpenEvent(EVENT_MODIFY_STATE, False, 'SpiffoCON-update-' + IntToStr(Pid));
  if Handle = 0 then
  begin
    Log('SpiffoCON is not waiting for this update: ' + IntToStr(Pid));
    Exit;
  end;
  SetEvent(Handle);
  CloseHandle(Handle);
  Handle := OpenProcess(SYNCHRONIZE, False, Pid);
  if Handle <> 0 then
  begin
    Log('Waiting for SpiffoCON to close: ' + IntToStr(WaitForSingleObject(Handle, 60000)));
    CloseHandle(Handle);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseUpdatedApp;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    CleanProgramFolder;
end;

#if Flavor == "Light"
{ Looks for a release (not preview) x64 .NET 10 Desktop Runtime. On ARM64 Windows the x64 runtime
  lives in dotnet\x64, the plain dotnet folder holds the native ARM64 one. }
function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  Root: String;
begin
  Result := False;
  if IsArm64 then
    Root := ExpandConstant('{commonpf64}\dotnet\x64')
  else
    Root := ExpandConstant('{commonpf64}\dotnet');
  if FindFirst(Root + '\shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then
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

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if IsDesktopRuntimeInstalled then
    Exit;
  case SuppressibleMsgBox(CustomMessage('RuntimeMissing'), mbConfirmation, MB_YESNOCANCEL, IDNO) of
    IDYES:
      begin
        ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/10.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
        Result := False;
      end;
    IDCANCEL:
      Result := False;
  end;
end;
#endif
