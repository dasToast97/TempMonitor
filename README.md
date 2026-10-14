# Toast Temperatur

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
| Energiesparend | Smart-Automatik, so leise wie möglich |
| Ausgewogen | Smart-Automatik, ruhig und kühl |
| Leistung | Smart-Automatik, kühl für Spiele & Rendern |
| Maximale Kühlung | Alle Lüfter 100 % |
| Eigene Kurve | Kurve pro Lüfter mit der Maus ziehen |

- Jeder Lüfter regelt nach **CPU, GPU und/oder Mainboard** (Häkchen auf der Karte) – das Bauteil,
  das gerade mehr Kühlung braucht, bestimmt die Drehzahl. Gehäuselüfter regeln standardmäßig nach CPU + GPU.
- **Mainboard-Grenze** (Standard 65 °C): Das Mainboard regelt nicht mit, dreht die Mainboard-Lüfter aber hoch,
  sobald es wärmer wird – so bleiben die Lüfter im Alltag leise.
- **Zieltemperaturen** für CPU, GPU und Mainboard sind je Profil einstellbar.
- Lüfter lassen sich **umbenennen**, als **Lüfter oder Pumpe** kennzeichnen und per **„Erkennen“**
  (5 Sekunden 100 %) zuordnen. Pumpen haben eine einstellbare Mindestleistung.
- Leere Anschlüsse ohne Lüfter werden ausgeblendet.
- Die **Smart-Automatik** lernt je Bauteil, welche Drehzahl bei welcher Auslastung die Zieltemperatur hält,
  und reagiert dadurch schon beim Lastanstieg. Gespeichert in `%AppData%\TempMonitor\fans.json`.

**Sicherheit (optional, standardmäßig aus):** Notkühlung mit einstellbaren Grenzwerten für CPU, GPU und Mainboard
und Stillstand-Erkennung. Beim Beenden oder Absturz übernimmt immer wieder das BIOS.

**AMD-Grafikkarten ab RX 6000:** Die Lüfter werden über AMD Overdrive 8 gesteuert (wie das Lüfter-Tuning
der AMD Software): Für die gewünschte Leistung werden alle 5 Kurvenpunkte auf diesen Wert gesetzt. Nur Lüfterwerte
und der Tuning-Modus werden verändert, nie Takt oder Spannung. Beim Beenden werden die ursprünglichen Werte
zurückgeschrieben. Lüfter, die trotzdem nicht reagieren, erkennt das Programm und meldet sie.

Mainboard-Lüfter brauchen den Treiber **PawnIO** (der Installer bietet ihn an). Hersteller-Tools
(Armoury Crate, MSI Center, iCUE …) sollten nicht gleichzeitig die Lüfter steuern.

### Hintergrund & Aufzeichnung

- Das **X schließt nicht**, sondern versteckt das Fenster – das Programm läuft im Infobereich weiter.
  Öffnen per Doppelklick auf das Symbol, **Beenden nur per Rechtsklick → Beenden**.
- **Aufzeichnung:** alle Temperaturen, Auslastungen, CPU-Leistung und Lüfter werden alle 5 Sekunden in
  `Dokumente\Toast Temperatur\Toast-Temperatur_JJJJ-MM-TT.csv` geschrieben (öffnet sich direkt in Excel).
  Ein-/ausschalten und Ordner öffnen über das Rechtsklick-Menü.
- **Mit Windows starten:** im Installer oder im Rechtsklick-Menü. Startet unsichtbar im Hintergrund.

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
