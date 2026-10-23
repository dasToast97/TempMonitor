## Neu in 2.18.0

**Lüfter (Sicherheit)**
- Toast Temperatur prüft beim Start, ob das BIOS alle Lüfter allein zum Drehen bringt. Steht ein Lüfter, obwohl das BIOS ihm Leistung gibt (Vorgabe unter seiner Anlauf-Grenze), gibt es einen deutlichen Hinweis im Reiter Lüfter und im Infobereich – mit der passenden BIOS-Einstellung zur Abhilfe.
- Beim Beenden bekommen solche Lüfter weiterhin eine sichere feste Leistung, damit sie bis zum nächsten Neustart nicht stehen.

**RGB: Lauflicht durch den ganzen PC**
- Gruppen haben jetzt einen **Ablauf**: *jeder Kanal für sich*, *durchgehend (LED für LED)* oder **Lüfter für Lüfter** – der Effekt springt von Lüfter zu Lüfter, z. B. ein Lauflicht von unten nach oben.
- **Reihenfolge** der Kanäle in einer Gruppe mit ▲ ▼ festlegen (z. B. unten → oben, vorne → hinten).
- Pro Kanal einstellbar, **wie viele Lüfter** daran hängen (bei hintereinander geschalteten Lüftern).
- Lauflicht (Komet) kann jetzt **hin und her** laufen.

**Nollie-Speicher**
- Eingebaute Nollie-Effekte laufen deutlich ruhiger (kleineres Tempo, Verzögerung und Bildzeit). Neue Einstellung „Bildzeit“ – höher = langsamer.
