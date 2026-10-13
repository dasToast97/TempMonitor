# Temperatur-Monitor

Kleines Windows-11-Programm, das alle verfügbaren Temperatursensoren
(CPU, Grafikkarte, Mainboard, RAM, SSDs/Festplatten) live ausliest und
für jeden Sensor einen Verlaufsgraphen der letzten 5 Minuten zeigt.

- Aktualisierung jede Sekunde
- Farbe je nach Temperatur: grün < 50 °C, gelb < 70 °C, orange < 85 °C, rot darüber
- Min/Max pro Sensor (Doppelklick auf eine Kachel setzt sie zurück)
- „Immer im Vordergrund“ zum Mitlaufen beim Spielen/Rendern
- **Eingebauter Updater**: sucht beim Start nach neuen GitHub Releases, fragt nach
  („Jetzt aktualisieren“ / „Später“ / „Diese Version überspringen“) und installiert sich selbst neu

### Lüftersteuerung (Reiter „Lüfter“)

| Profil | Was es macht |
|---|---|
| Standard (BIOS) | Programm greift nicht ein |
| Energiesparend | Smart-Automatik, Ziel CPU ~78 °C, so leise wie möglich |
| Ausgewogen | Smart-Automatik, Ziel CPU ~68 °C |
| Leistung | Smart-Automatik, Ziel CPU ~58 °C |
| Maximale Kühlung | Alle Lüfter 100 % |
| Eigene Kurve | Kurve pro Lüfter mit der Maus ziehen |

Die **Smart-Automatik** lernt, welche Drehzahl bei welcher Auslastung die Zieltemperatur hält,
reagiert dadurch schon beim Lastanstieg und passt ihre Reaktionsstärke selbst an (kein Auf- und Abheulen).
Das Gelernte wird in `%AppData%\TempMonitor\fans.json` gespeichert.

**Sicherheit:** ab 90 °C CPU / 85 °C GPU sofort 100 % · nie unter die Mindestdrehzahl · Lüfter, die trotz
Befehl stehen bleiben, gehen zurück ans BIOS · beim Beenden oder Absturz übernimmt wieder das BIOS.
Bei aktiver Steuerung läuft das Programm beim Schließen im Infobereich weiter.

Mainboard-Lüfter brauchen den Treiber **PawnIO**. Hersteller-Tools (Armoury Crate, MSI Center, iCUE …)
sollten nicht gleichzeitig die Lüfter steuern.

Sensoren kommen aus [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor).

## Benutzen

1. Unter **Releases** die neueste `TempMonitor-Setup.exe` herunterladen und ausführen.
2. Der Installer legt das Programm in „Programme“ ab, erstellt einen Startmenü-Eintrag,
   auf Wunsch eine Desktop-Verknüpfung und Autostart – und eine Deinstallation unter
   „Einstellungen → Apps“.
3. Beim Start fragt Windows nach Administratorrechten (nötig für CPU- und Mainboard-Sensoren).
4. Fehlen CPU- oder Mainboard-Werte: den Treiber **PawnIO** von https://pawnio.eu installieren
   und das Programm neu starten.

Ohne Installation geht es auch: `TempMonitor.exe` aus dem Release direkt starten (portable).

Windows SmartScreen kann beim ersten Start warnen, weil die Dateien nicht signiert sind:
„Weitere Informationen“ → „Trotzdem ausführen“.

## Neue Version veröffentlichen

Der Workflow in `.github/workflows/release.yml` erledigt alles automatisch.
Du musst nur einen Versions-Tag pushen:

```bash
git add .
git commit -m "Was sich geändert hat"
git tag v1.1.0
git push && git push --tags
```

GitHub baut dann `TempMonitor-Setup.exe` (plus portable `TempMonitor.exe`) und erstellt das Release. Alle installierten Programme
bieten das Update beim nächsten Start an.

Die Versionsnummer kommt nur aus dem Tag – sie muss nirgends im Code geändert werden.

## Selbst bauen (optional)

Benötigt das [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet run
```

Lokal gebaute Versionen haben keine Update-Quelle; der Updater ist dort deaktiviert.
