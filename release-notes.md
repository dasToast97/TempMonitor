## Neu in 2.25.3

- **„Eigene Kurve“ wieder wie vor 2.25.2:** Gehäuse- und Radiator-Lüfter mit Quelle „GPU“ beachten den GPU-Hot-Spot wieder auf ihrer Kurve.
- Beibehalten: Fahren Lüfter wegen Notkühlung oder Mainboard-Grenze hoch, steht der Grund im Fehler-Logbuch und auf der Lüfter-Karte. Das Diagnose-Paket enthält die Temperatur-Aufzeichnungen von heute und gestern.

## Neu in 2.25.2

**Lüfter drehen nicht mehr unnötig voll auf**
- Bei „Eigene Kurve“ haben Gehäuse- und Radiator-Lüfter mit Quelle „GPU“ auch den **GPU-Hot-Spot** auf ihre Kurve gelegt. Der Hot Spot liegt 15–30 °C über der GPU-Temperatur – die Kurve lief dadurch beim Spielen schnell auf 80–100 %. Jetzt bestimmt bei diesen Lüftern die GPU-Temperatur die Kurve; der Hot Spot zählt nur noch für die Notkühlung (ab der eingestellten Grenze, Standard 105 °C). Der Grafikkarten-Lüfter selbst regelt weiter nach dem Hot Spot.
- **Nachvollziehbar:** Fahren Lüfter wegen Notkühlung oder Mainboard-Grenze hoch, steht jetzt im Fehler-Logbuch, warum und bei welchen Temperaturen. Die Lüfter-Karte zeigt den Grund direkt an (z. B. „Notkühlung – Grafikkarte 86 °C ≥ 85 °C“).
- Das **Diagnose-Paket** enthält jetzt auch die Temperatur-Aufzeichnungen von heute und gestern.
