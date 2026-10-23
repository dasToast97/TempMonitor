## Neu in 2.15.3

**Energiesparmodus**
- Vor dem Energiesparmodus gibt Toast Temperatur alle Lüfter ans BIOS zurück – ein im Leerlauf angehaltener Lüfter bleibt dadurch nicht aus.
- Nach dem Aufwachen startet Toast Temperatur sich selbst sauber neu (Sensoren, Lüfter, RGB und OpenRGB werden frisch eingelesen).

**Lüfter werden nicht mehr „vergessen“**
- Anschlüsse, an denen schon einmal ein Lüfter lief, bleiben sichtbar – auch wenn der Lüfter beim Start gerade steht (Leerlauf-Stopp, Absturz, Energiesparmodus). Mit „Entfernen“ lässt sich so ein Anschluss bei Bedarf ausblenden.

**RGB: Tastatur/Maus wieder finden**
- „Geräte neu suchen“ (jetzt direkt im RGB-Reiter) startet auch OpenRGB neu – danach tauchen Tastatur und Maus wieder auf.
- Ein von einem früheren Lauf übrig gebliebenes OpenRGB wird beim Start beendet und frisch gestartet.
