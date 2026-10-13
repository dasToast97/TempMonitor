; Installer für den Temperatur-Monitor (Inno Setup 6)
; Wird automatisch vom GitHub-Workflow gebaut. Version kommt per /DMyAppVersion=x.y.z

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyRepo
  #define MyRepo ""
#endif

#define MyAppName "Temperatur-Monitor"
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
Name: "autostart"; Description: "Beim Windows-Start automatisch öffnen"; GroupDescription: "Zusätzliche Optionen:"; Flags: unchecked

[Files]
Source: "..\publish\{#MyAppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\{#MyAppName} deinstallieren"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Registry]
; Autostart: über die Aufgabenplanung wäre ohne UAC-Abfrage, aber das hier ist einfacher und transparent
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TempMonitor"; ValueData: """{app}\{#MyAppExe}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
; Nach normaler Installation: Häkchen "Programm starten"
Filename: "{app}\{#MyAppExe}"; Description: "Temperatur-Monitor jetzt starten"; Flags: nowait postinstall skipifsilent shellexec
; Nach automatischem Update (stille Installation): direkt wieder starten
Filename: "{app}\{#MyAppExe}"; Flags: nowait shellexec; Check: WizardSilent
