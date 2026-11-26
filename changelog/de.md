# Änderungsprotokoll

## [1.10.0]

An Crimson Desert 2.06.00 angepasst.

### Neu

- **Aus SaveUltimate wird CDSE** (Crimson Desert Save Editor), mit neuem Logo, neuen Farben und Schriften. Standarddesign ist CDSE Obsidian; das bisherige gibt es weiter als Kohle und Gold. Nach dem Update von 1.9.0 heißt die Programmdatei bis zum nächsten Update weiter `SaveUltimate.exe`, danach `CDSE.exe`. Fehlercodes beginnen jetzt mit `CDSE-` statt `SU-`; die Nummern bleiben gleich.
- **Verlauf:** Alle Änderungen stehen rechts. Ein Klick führt zu diesem Zeitpunkt zurück oder wieder vor (Strg+Y). Speichern behält ihn.
- **Gegenstände → Gegenstandsdatenbank:** gewöhnliche Gegenstände ins Inventar legen, eine Art oder mehrere auf einmal. Die nötigen Plätze werden vorher gezeigt; Questgegenstände, Schlüssel und Schriften gehen nicht.
- **Inventar:** Wird die Menge eines nicht stapelbaren Gegenstands erhöht, etwa Ausrüstung, entstehen ebenso viele einzelne Kopien mit gleicher Verstärkung, Sockeln und Färbung.
- **Ausrüstung:** Jeder Sockel zeigt seine Ausrüstung mit Bild, wie bei Haustieren, und die Auswahl listet nur passende Abyss-Ausrüstung, jeweils mit ihrer Wirkung: dem Wert einer Werte-Ausrüstung (Angriff +2, Bewegungstempo +4 %) oder dem Effekt einer Fertigkeits-Ausrüstung, mit der Spielbeschreibung beim Darüberfahren. Gegenstandslisten haben keine Spalte Platz mehr.
- **Neues Menü Haustiere und Transportmittel:** Haustiere, Pferde, besondere Transportmittel, Wagen und Segelschiffe hinzufügen und entfernen. Ein Schiff kommt mit Segel und Kanone. Beim Entfernen wird auch die Ausrüstung gelöscht. Lässt sich etwas nicht entfernen, steht der Grund dabei: Haupttier, von einer Quest genannt oder beladen.
- **Haustiere und Transportmittel → Ausrüstung:** Ausrüstung von Haustieren, Pferden, besonderen Transportmitteln, Schiffen und A.T.A.G. wechseln, auch Schiffskanonen und A.T.A.G.-Waffen. Sie wird neu erstellt und ersetzt die getragene.
- **Charakter → Färbung:** Farbe, Färbemittel, Material und Schmutz jedes Teils ansehen. Auswahl oder das Set von Kliff, Oongka oder Damiane in eine Datei oder als Code zum Einfügen in eine Nachricht exportieren und beides importieren. Für jeden Eintrag wählen, welcher eigene Gegenstand die Färbung bekommt – mit Ort, Verbesserung und aktueller Färbung –, oder ihn bereits gefärbt neu erstellen.
- **Alltag → Wohn-System:** ein ganzes frei gebautes Haus um 180° drehen.

### Behoben

- Beim Auswählen einer Zelle wird ihr Text nicht mehr abgeschnitten.
- Wissen: Das Stufenfeld wird nach dem Anwenden nicht mehr leer.
- Alle Söldner zeigen ein Gesichtsporträt wie in der Söldnerliste des Spiels; 46 ohne Bild haben jetzt eines.
- Fähigkeiten: Gegenstände angeln wird nicht mehr gelistet; es scheint im Spiel noch nicht umgesetzt zu sein.

### Sonstiges

- Neues Menü Charakter mit Ausrüstung, Fähigkeiten und Färbung.
- Bilder von Haustieren, Transportmitteln und Fischen sind größer, ohne Hintergrund und je Farbe verschieden.
- Haustiere und Transportmittel zeigt kein Vieh, keine gefangenen Fische und keine internen Daten mehr.
- Weniger Schaltflächen, zwei pro Zeile; Seltenes steht im Menü ⋯.
- Alle Fenster haben die Titelleiste des Editors.
- Synchronisierte Spielbilder belegen etwa ein Drittel des Speicherplatzes (590 MB → 195 MB), und die Synchronisierung ist schneller. Beim ersten Start nach dem Update wird einmal neu synchronisiert.

## [1.9.0]

### Neu

- **Alltag → Wohn-System:** DLC-Grundstücke freischalten, Häuser exportieren und importieren.
- **NPC → Haustiere und Transportmittel:** Schiffe neu anordnen.
- **Französisch, Deutsch, Spanisch und brasilianisches Portugiesisch.**
- Update-Hinweise erscheinen in der Sprache des Editors.

### Behoben

- Wissen: „Gelernt durch“ zeigte direkt nach dem Öffnen eines Spielstands „Kein Eintrag“.
- Beim Wiederherstellen einer Sicherung bleibt jetzt auch eine Kopie im Ordner `backups/` des Editors.

### Sonstiges

- Die Hilfe der Tabs ist klarer. Die Hilfe zu Sicherung/Wiederherstellung nennt, dass pro Spielstand die 10 neuesten Sicherungen bleiben.
