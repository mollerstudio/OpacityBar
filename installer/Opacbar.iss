; Opacbar installer (Inno Setup 6). Per-user, no admin.
;
;   iscc installer\Opacbar.iss                         (build.ps1 does this when ISCC.exe is installed)
;   iscc /DAppVersion=1.2.3 /DSourceDir=..\out installer\Opacbar.iss
;
; Expects dist\Opacbar.exe, dist\OpacbarTAP.x64.dll and dist\OpacbarTAP.arm64.dll.
; Output: dist\OpacbarSetup.exe

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist"
#endif
#define AppName "Opacbar"
#define AppExe "Opacbar.exe"
; Same value name and format as AppConfig.SetStartup in src\AppConfig.cs.
#define RunKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define RunValue "Opacbar"

[Setup]
AppId={{8E4C2B71-5D3A-4F6E-9B12-7A0C3D5E9F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Opacbar contributors
VersionInfoVersion={#AppVersion}
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
LicenseFile=..\LICENSE
OutputDir={#SourceDir}
OutputBaseFilename=OpacbarSetup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
CloseApplications=no
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Opacbar when Windows starts"

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\OpacbarTAP.x64.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\OpacbarTAP.arm64.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "{#RunKey}"; ValueType: string; ValueName: "{#RunValue}"; \
    ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Opacbar"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Helper copies and the optional diagnostics log. A copy Explorer still has loaded stays until sign-out.
Type: filesandordirs; Name: "{localappdata}\Opacbar"

[Code]
// Asks a running Opacbar to quit and restore the taskbar ("Opacbar.exe --quit" waits up to 5 s),
// then falls back to taskkill.
procedure CloseOpacbar(const Exe: String);
var
  Code: Integer;
begin
  Code := -1;
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  if Code <> 0 then
  begin
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sleep(500);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // The new exe knows --quit even when the running copy is portable or not installed yet.
  ExtractTemporaryFile('{#AppExe}');
  CloseOpacbar(ExpandConstant('{tmp}\{#AppExe}'));
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // Unticked on an upgrade: remove an entry an earlier install (or the app itself) made.
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('startup') then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');
end;

function InitializeUninstall(): Boolean;
begin
  CloseOpacbar(ExpandConstant('{app}\{#AppExe}'));
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Also covers an entry made by the app's own "Start with Windows" toggle.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, '{#RunKey}', '{#RunValue}');
end;

// Minimal monochrome wizard: no side or corner images, text uses the full width.
procedure InitializeWizard;
var
  Shift: Integer;
begin
  WizardForm.WizardSmallBitmapImage.Visible := False;
  WizardForm.PageNameLabel.Width := WizardForm.MainPanel.ClientWidth - WizardForm.PageNameLabel.Left - ScaleX(16);
  WizardForm.PageDescriptionLabel.Width := WizardForm.MainPanel.ClientWidth - WizardForm.PageDescriptionLabel.Left - ScaleX(16);

  WizardForm.WizardBitmapImage2.Visible := False;
  Shift := WizardForm.FinishedHeadingLabel.Left - ScaleX(32);
  WizardForm.FinishedHeadingLabel.Left := WizardForm.FinishedHeadingLabel.Left - Shift;
  WizardForm.FinishedHeadingLabel.Width := WizardForm.FinishedHeadingLabel.Width + Shift;
  WizardForm.FinishedLabel.Left := WizardForm.FinishedLabel.Left - Shift;
  WizardForm.FinishedLabel.Width := WizardForm.FinishedLabel.Width + Shift;
  WizardForm.RunList.Left := WizardForm.RunList.Left - Shift;
  WizardForm.RunList.Width := WizardForm.RunList.Width + Shift;
end;
