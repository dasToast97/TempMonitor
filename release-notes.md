## Neu in 2.26.0

**Vorrang beim Kühlen: Hauptkühler zuerst, Gehäuse-Lüfter helfen**
Gilt für „Energiesparend“, „Ausgewogen“ und „Leistung“. Notkühlung und Mainboard-Grenze bleiben unverändert.
- **CPU:** Der Radiator-Lüfter kühlt zuerst. Gehäuse-Lüfter helfen erst mit, wenn der Radiator **65 %** erreicht (einstellbar unter „Gehäuse-Lüfter helfen ab“), und steigen dann langsam ein (höchstens 1,5 % pro Sekunde, gleichmäßig mit dem Radiator bis 100 %).
- **Grafikkarte:** Der Grafikkarten-Lüfter kühlt zuerst. Gehäuse-Lüfter helfen ebenso erst ab 65 % mit.
- **Pumpe:** Hält dauerhaft ihre Drehzahl („Pumpe mindestens“) und dreht nur im Notfall mit hoch, ab 90 % des Radiators.
- **Wasserkühlung ist träge:** Bei der CPU wird die Temperatur ruhiger ausgewertet. Kurze Spitzen (z. B. 59 → 63 °C für eine Minute) treiben Radiator und Gehäuse-Lüfter nicht mehr sofort hoch.
- Auf jeder Lüfter-Karte: **„Vorrang“** – automatisch, Hauptkühler oder Helfer. Automatisch erkennt den Radiator am Namen (Radiator, AIO, CPU) oder an der gemessenen Wirkung auf die CPU.
- Die Lüfter-Karte zeigt, ob ein Helfer gerade wartet oder mithilft.

**Aufzeichnung**
- Kam im Lauf des Tages ein Sensor dazu, rutschten die Werte in der CSV in die falsche Spalte. Jetzt wird dann eine neue Kopfzeile geschrieben, und der Verlauf liest auch die älteren Dateien richtig ein.

## Neu in 2.25.3

- **„Eigene Kurve“ wieder wie vor 2.25.2:** Gehäuse- und Radiator-Lüfter mit Quelle „GPU“ beachten den GPU-Hot-Spot wieder auf ihrer Kurve.
- Beibehalten: Fahren Lüfter wegen Notkühlung oder Mainboard-Grenze hoch, steht der Grund im Fehler-Logbuch und auf der Lüfter-Karte. Das Diagnose-Paket enthält die Temperatur-Aufzeichnungen von heute und gestern.

## Neu in 2.25.2

**Lüfter drehen nicht mehr unnötig voll auf**
- Bei „Eigene Kurve“ haben Gehäuse- und Radiator-Lüfter mit Quelle „GPU“ auch den **GPU-Hot-Spot** auf ihre Kurve gelegt. Der Hot Spot liegt 15–30 °C über der GPU-Temperatur – die Kurve lief dadurch beim Spielen schnell auf 80–100 %. Jetzt bestimmt bei diesen Lüftern die GPU-Temperatur die Kurve; der Hot Spot zählt nur noch für die Notkühlung (ab der eingestellten Grenze, Standard 105 °C). Der Grafikkarten-Lüfter selbst regelt weiter nach dem Hot Spot.
- **Nachvollziehbar:** Fahren Lüfter wegen Notkühlung oder Mainboard-Grenze hoch, steht jetzt im Fehler-Logbuch, warum und bei welchen Temperaturen. Die Lüfter-Karte zeigt den Grund direkt an (z. B. „Notkühlung – Grafikkarte 86 °C ≥ 85 °C“).
- Das **Diagnose-Paket** enthält jetzt auch die Temperatur-Aufzeichnungen von heute und gestern.
