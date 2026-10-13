; Installer für Toast Temperatur (Inno Setup 6)
; Wird automatisch vom GitHub-Workflow gebaut. Version kommt per /DMyAppVersion=x.y.z

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyRepo
  #define MyRepo ""
#endif

#define MyAppName "Toast Temperatur"
#define MyAppExe  "TempMonitor.exe"

[Setup]
AppId={{6B3C2E1A-9F4D-4C7B-A2E8-5D1F0C3B7A91}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=TempMonitor
#if MyRepo != ""
AppPublisherURL=https://github.com/{#MyRepo}
AppUpdatesURL=https://github.com/{#MyRepo}/releases
#endif
DefaultDirName={autopf}\TempMonitor
DefaultGroupName={#MyAppName}
UsePreviousGroup=no
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\publish
OutputBaseFilename=TempMonitor-Setup
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Läuft das Programm noch, wird es zum Aktualisieren geschlossen
AppMutex=TempMonitor_SingleInstance
CloseApplications=force
RestartApplications=no

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[Tasks]
Name: "desktopicon"; Description: "Verknüpfung auf dem Desktop erstellen"; GroupDescription: "Zusätzliche Verknüpfungen:"
Name: "pawnio"; Description: "Treiber PawnIO mitinstallieren (nötig für CPU- und Mainboard-Werte und Mainboard-Lüfter, wird von pawnio.eu geladen)"; GroupDescription: "Treiber:"; Check: ShowPawnIoTask
Name: "autostart"; Description: "Beim Windows-Start im Infobereich starten (nötig, damit die Lüftersteuerung immer aktiv ist)"; GroupDescription: "Zusätzliche Optionen:"; Flags: unchecked

[Files]
Source: "..\publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Verknüpfungen mit dem alten Namen entfernen
Type: files; Name: "{autoprograms}\Temperatur-Monitor\Temperatur-Monitor.lnk"
Type: files; Name: "{autoprograms}\Temperatur-Monitor\Temperatur-Monitor deinstallieren.lnk"
Type: files; Name: "{autodesktop}\Temperatur-Monitor.lnk"
Type: dirifempty; Name: "{autoprograms}\Temperatur-Monitor"

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\{#MyAppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
; Treiber PawnIO (vorher im Assistenten von der offiziellen Quelle heruntergeladen)
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install"; StatusMsg: "Installiere Treiber PawnIO …"; Flags: waituntilterminated; Tasks: pawnio; Check: PawnIoDownloaded
; Autostart über die Aufgabenplanung: startet mit Adminrechten ohne UAC-Abfrage bei der Anmeldung
Filename: "{sys}\schtasks.exe"; Parameters: "/Create /F /TN ""TempMonitor"" /TR ""\""{app}\{#MyAppExe}\"" /tray"" /SC ONLOGON /RL HIGHEST"; Flags: runhidden; Tasks: autostart
; Kein 72-Stunden-Zeitlimit, auch im Akkubetrieb laufen lassen
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$s = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries; Set-ScheduledTask -TaskName 'TempMonitor' -Settings $s"""; Flags: runhidden; Tasks: autostart
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""TempMonitor"""; Flags: runhidden; Tasks: not autostart
; Nach normaler Installation: Häkchen "Programm starten"
Filename: "{app}\{#MyAppExe}"; Description: "{#MyAppName} jetzt starten"; Flags: nowait postinstall skipifsilent shellexec
; Nach automatischem Update (stille Installation): direkt wieder starten
Filename: "{app}\{#MyAppExe}"; Flags: nowait shellexec; Check: WizardSilent

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /F /TN ""TempMonitor"""; Flags: runhidden; RunOnceId: "DelTask"

[Code]
const
  PawnIoUrl = 'https://github.com/namazso/PawnIO.Setup/releases/latest/download/PawnIO_setup.exe';
  PawnIoKey = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO';

var
  DownloadPage: TDownloadWizardPage;
  Downloaded: Boolean;

function PawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, PawnIoKey) or RegKeyExists(HKLM32, PawnIoKey);
end;

{ Nur anbieten, wenn der Treiber fehlt – und nicht bei stillen Auto-Updates }
function ShowPawnIoTask: Boolean;
begin
  Result := (not PawnIoInstalled) and (not WizardSilent);
end;

function PawnIoDownloaded: Boolean;
begin
  Result := Downloaded;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and WizardIsTaskSelected('pawnio') then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(PawnIoUrl, 'PawnIO_setup.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
        Downloaded := True;
      except
        SuppressibleMsgBox('Der Treiber PawnIO konnte nicht heruntergeladen werden:' + #13#10 +
          GetExceptionMessage + #13#10#13#10 +
          'Toast Temperatur wird trotzdem installiert. Den Treiber kannst du später im Programm installieren.',
          mbError, MB_OK, IDOK);
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;
