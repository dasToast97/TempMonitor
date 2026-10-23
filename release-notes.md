## Neu in 2.15.1

**Kalibrierung überarbeitet**
- Misst jetzt zwei Werte: **Anlauf** (ab wie viel % der Lüfter aus dem Stand anläuft, in feinen 3-%-Schritten mit mehr Zeit je Schritt) und **Weiterlauf** (bis wie weit herunter er danach weiterdreht).
- Der Anlauf-Wert wird nur noch kurz zum Anschieben benutzt (nur so viel wie nötig, nicht mehr pauschal 50 %) – danach regelt der Lüfter bis zur Weiterlauf-Grenze herunter.
- Bleibt ein Lüfter im Betrieb doch stehen, wird er sofort angeschoben und die Untergrenze lernt automatisch etwas nach.
- Bereits kalibrierte Lüfter: Die Untergrenze wird vorerst aus dem alten Wert geschätzt. Für genaue Werte einmal neu kalibrieren (Lüfter → Erweiterte Einstellungen → Kalibrierung).
