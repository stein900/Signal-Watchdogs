; =====================================================================
;  Signal Watchdogs — script d'installation (Inno Setup 7)
;  Généré par build.ps1 :  ISCC /DAppVersion=x.y.z /DSourceExe=<chemin> SignalWatchdogs.iss
;
;  Installation silencieuse (GPO / Intune / SCCM / script) :
;    SignalWatchdogs-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART [/LOGDIR=\\serveur\wd-logs$] [/MERGETASKS="!autostart"]
;  Désinstallation silencieuse :
;    "C:\Program Files\Signal Watchdogs\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; =====================================================================

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\bin\Release\net48\SignalWatchdogs.exe"
#endif

[Setup]
AppId={{4E6DE550-04F0-4006-81CB-8282D2543315}
AppName=Signal Watchdogs
AppVersion={#AppVersion}
AppVerName=Signal Watchdogs {#AppVersion}
AppPublisher=Signal Watchdogs
AppComments=Surveillance des écrans noirs : écrans, cartes graphiques, USB/USB-C, sessions RDP
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Installation de Signal Watchdogs
; Installation par machine dans Program Files : dossier non modifiable par les utilisateurs (sécurité).
; Le choix du dossier est volontairement désactivé : un dossier hors Program Files hériterait souvent
; de droits d'écriture pour tous les utilisateurs, ce qui permettrait de remplacer l'exécutable.
PrivilegesRequired=admin
DefaultDirName={autopf}\Signal Watchdogs
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
WizardStyle=modern dynamic
SetupIconFile=..\assets\SignalWatchdogs.ico
UninstallDisplayIcon={app}\SignalWatchdogs.exe
UninstallDisplayName=Signal Watchdogs
OutputDir=..\dist
OutputBaseFilename=SignalWatchdogs-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "autostart"; Description: "Lancer Signal Watchdogs automatiquement à l'ouverture de session (tous les utilisateurs)"; GroupDescription: "Démarrage :"
Name: "desktopicon"; Description: "Créer une icône sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Dirs]
; Journaux communs au poste : chaque utilisateur (non administrateur) doit pouvoir y écrire.
; Une instance lancée en administrateur n'y écrit jamais (elle utilise un dossier réservé aux admins).
Name: "{commonappdata}\Signal Watchdogs\Logs"; Permissions: users-modify; Flags: uninsneveruninstall

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LISEZMOI.txt"; DestDir: "{app}"; Flags: ignoreversion

[INI]
Filename: "{app}\SignalWatchdogs.ini"; Section: "SignalWatchdogs"; Key: "LogDirectory"; String: "{code:GetLogDir}"; Check: HasLogDir

[Registry]
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SignalWatchdogs"; ValueData: """{app}\SignalWatchdogs.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Icons]
Name: "{autoprograms}\Signal Watchdogs"; Filename: "{app}\SignalWatchdogs.exe"; Comment: "Surveillance des écrans, GPU et USB"
Name: "{autodesktop}\Signal Watchdogs"; Filename: "{app}\SignalWatchdogs.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser : l'application est lancée avec les droits de l'utilisateur, pas ceux de l'installateur.
Filename: "{app}\SignalWatchdogs.exe"; Description: "Lancer Signal Watchdogs maintenant"; Flags: nowait postinstall skipifsilent runasoriginaluser

#ifndef TEST_NOKILL
[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM SignalWatchdogs.exe"; Flags: runhidden; RunOnceId: "StopSignalWatchdogs"
#endif

[UninstallDelete]
Type: files; Name: "{app}\SignalWatchdogs.ini"
Type: filesandordirs; Name: "{app}\SignalWatchdogs_Logs"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  LogPage: TInputQueryWizardPage;

function InitializeSetup: Boolean;
var
  Release: Cardinal;
begin
  Result := True;
  { .NET Framework 4.8 (intégré à Windows 10 1903+ et Windows 11) }
  if not RegQueryDWordValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full', 'Release', Release) or (Release < 528040) then
  begin
    SuppressibleMsgBox('Signal Watchdogs nécessite .NET Framework 4.8, absent de ce poste.' + #13#10 +
      'Installez-le (Windows Update) puis relancez l''installation.', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

procedure InitializeWizard;
var
  Initial: String;
begin
  LogPage := CreateInputQueryPage(wpSelectTasks,
    'Journaux', 'Où Signal Watchdogs doit-il enregistrer ses journaux ?',
    'Laissez vide pour des journaux locaux (C:\ProgramData\Signal Watchdogs\Logs).' + #13#10#13#10 +
    'Pour regrouper plusieurs postes (onglet « Vue parc »), indiquez un partage réseau DÉDIÉ aux journaux, ' +
    'par exemple \\serveur\wd-logs$, où chaque poste ne peut écrire que dans son propre dossier.');
  LogPage.Add('Dossier des journaux (facultatif) :', False);
  Initial := ExpandConstant('{param:LOGDIR|}');
  if Initial = '' then
    Initial := GetPreviousData('LogDir', '');
  LogPage.Values[0] := Initial;
end;

function GetLogDir(Param: String): String;
begin
  Result := Trim(LogPage.Values[0]);
end;

function HasLogDir: Boolean;
begin
  Result := GetLogDir('') <> '';
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  V: String;
begin
  Result := True;
  if CurPageID = LogPage.ID then
  begin
    V := GetLogDir('');
    if (Pos('"', V) > 0) or (Pos(#13, V) > 0) or (Pos(#10, V) > 0) then
    begin
      MsgBox('Le chemin contient des caractères invalides.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'LogDir', GetLogDir(''));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  { Arrête les instances en cours (toutes sessions) pour pouvoir remplacer l'exécutable. }
#ifndef TEST_NOKILL
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM SignalWatchdogs.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
#endif
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    { Réinstallation : retire ce qui n'est plus demandé. }
    if not WizardIsTaskSelected('autostart') then
      RegDeleteValue(HKEY_LOCAL_MACHINE_64, RunKey, 'SignalWatchdogs');
    if not HasLogDir then
      DeleteIniEntry('SignalWatchdogs', 'LogDirectory', ExpandConstant('{app}\SignalWatchdogs.ini'));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Démarrage automatique éventuellement activé par l'utilisateur depuis l'application. }
    if RegQueryStringValue(HKEY_CURRENT_USER, RunKey, 'SignalWatchdogs', Value) and (Pos(LowerCase(ExpandConstant('{app}')), LowerCase(Value)) > 0) then
      RegDeleteValue(HKEY_CURRENT_USER, RunKey, 'SignalWatchdogs');
    if (not UninstallSilent) and
       (MsgBox('Supprimer aussi les journaux enregistrés sur ce poste (C:\ProgramData\Signal Watchdogs) ?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES) then
      DelTree(ExpandConstant('{commonappdata}\Signal Watchdogs'), True, True, True);
  end;
end;
