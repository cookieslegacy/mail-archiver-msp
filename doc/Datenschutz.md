# Datenschutz (DSGVO)

Dieser Fork archiviert E-Mails, weil das Gesetz es verlangt (§ 147 AO, § 257 HGB). Die
Rechtsgrundlage der Verarbeitung ist damit **Art. 6 Abs. 1 lit. c DSGVO**. Diese Seite
beschreibt, was die Software für die Pflichten aus der DSGVO leistet, was sie bewusst nicht
tut und was Sie als Verantwortlicher oder Auftragsverarbeiter selbst regeln müssen.
**Keine Rechtsberatung.**

## Rollen

| | |
|---|---|
| **Verantwortlicher** (Art. 4 Nr. 7) | der Kunde (die Organisation), dessen Postfächer archiviert werden |
| **Auftragsverarbeiter** (Art. 28) | der Dienstleister, der die Installation betreibt (Provider-Admin) |
| **Betroffene** | Mitarbeiter des Kunden sowie alle, die mit ihm E-Mails wechseln |

Der Dienstleister braucht mit jedem Kunden einen **AV-Vertrag** (Art. 28 Abs. 3). Die
Mandantentrennung, der freigabepflichtige und protokollierte Support-Zugriff und der auf
Verwaltung beschränkte Zugriff des Providers (siehe [Mandanten](Organizations.md)) sind die
technische Seite davon; der Vertrag ist Ihre Aufgabe.

## Was die Software leistet

### Betroffenenauskunft (Art. 15)

*Compliance → Auskunft und Einschränkungen*

Adresse eingeben; die Software findet alle E-Mails der Organisation, in denen die Person als
Absender oder Empfänger vorkommt, über alle Postfächer hinweg (Admins und Prüfer). Dazu:

* **Übersicht (CSV)** mit Datum, Postfach, Absender, Empfänger, Betreff, Anhängen,
* **Export (EML, ZIP)** als Kopie der Daten (Art. 15 Abs. 3, Art. 20).

Jede Abfrage und jeder Export landet im Audit-Log, damit die Beantwortung einer Anfrage
nachweisbar ist (Art. 5 Abs. 2, Rechenschaftspflicht). Die Auskunft selbst (Zwecke,
Empfänger, Speicherdauer, Rechte) schreiben Sie; die Speicherdauer ergibt sich aus den
Aufbewahrungsfristen der Postfächer.

### Löschung (Art. 17)

Innerhalb der gesetzlichen Aufbewahrungsfrist ist eine Löschung technisch ausgeschlossen
(Datenbank-Trigger, WORM-Speicher). Das ist durch **Art. 17 Abs. 3 lit. b** gedeckt:
Die Pflicht zur Aufbewahrung geht dem Löschrecht vor. Nach Ablauf der Frist löscht die
Software – automatisch, wenn *Abgelaufene E-Mails automatisch löschen* aktiv ist. Der
Löschvorgang ist in der Hash-Kette nachweisbar (die Originalbytes werden entfernt, der
Kettendatensatz bleibt als Löschnachweis).

**Nicht aufbewahrungspflichtige Postfächer** (z. B. rein private Nutzung) stellen Sie auf
0 Jahre; dann ist Löschen jederzeit möglich. Die Zuordnung, welches Postfach welcher
Frist unterliegt, ist eine Entscheidung des Verantwortlichen, nicht der Software.

### Einschränkung der Verarbeitung (Art. 18)

Wenn eine Person widerspricht oder die Richtigkeit bestreitet und eine Löschung (noch)
nicht möglich ist, kann ein Admin einzelne E-Mails **einschränken** (auf der E-Mail-Seite
oder gesammelt aus der Auskunft heraus, mit Begründung). Eingeschränkte E-Mails

* bleiben archiviert, mit Frist und Kettenglied,
* sind für alle Benutzer außer Admins und Prüfern unsichtbar, auch in der Suche,
* sind auf der Datenschutzseite gelistet und werden protokolliert (Setzen und Aufheben).

Admin-Exporte (Konto-Export, Audit-Export für die Betriebsprüfung) enthalten eingeschränkte
E-Mails weiterhin – das ist die Ausnahme „Geltendmachung von Rechtsansprüchen“ bzw. die
gesetzliche Pflicht (Art. 18 Abs. 2).

### Speicherbegrenzung (Art. 5 Abs. 1 lit. e)

* **E-Mails:** Fristen pro Organisation und Postfach, Löschung nach Ablauf.
* **Audit-Log:** Das Protokoll enthält Benutzernamen, Suchbegriffe und bei Anmeldungen
  IP-Adressen. Es ist unveränderbar und hash-verkettet; Einträge älter als
  `Compliance:AuditLogRetentionYears` (Standard **10 Jahre**, 0 = nie) werden täglich
  entfernt. Entfernt wird immer nur der älteste Abschnitt der Kette; die Schnittstelle
  (letzte Nummer und Hash) wird festgehalten, und die Integritätsprüfung setzt dort auf.
  Die Frist sollte nicht kürzer sein als die längste Aufbewahrungsfrist der E-Mails, weil
  das Protokoll zum Nachweis der ordnungsgemäßen Archivierung gehört (GoBD Rz. 151 ff.).
* **Exporte:** Fertige Export-Dateien werden nach `AuditExport:RetentionDays` gelöscht.

### Sicherheit der Verarbeitung (Art. 32)

Vorhanden: Rollen und Mandantentrennung (zwei Ebenen), 2FA, OIDC, API-Keys mit
Berechtigungen, verschlüsselte Postfach-Zugangsdaten, hash-verkettete Protokolle, optional
WORM-Speicher und Zeitstempel, Backup-Anleitung des Originals.

**Nicht** von der Software geleistet und daher Teil Ihrer TOM:

* Verschlüsselung der E-Mails in der Datenbank – die Inhalte liegen im Klartext in
  PostgreSQL. Üblich ist Festplatten-/Volume-Verschlüsselung des Servers.
* Transportverschlüsselung – HTTPS über den Reverse-Proxy.
* Zugriff auf Betriebssystem, Datenbank und Backups.

## Was Sie selbst erledigen müssen

| Pflicht | Wer | Hilfe |
|---|---|---|
| AV-Vertrag je Kunde (Art. 28) | Dienstleister | Anlage: [TOM-Liste unten](#anlage-technische-und-organisatorische-massnahmen-tom) |
| Verzeichnis der Verarbeitungstätigkeiten (Art. 30) | Kunde und Dienstleister | [Vorlage unten](#anlage-eintrag-im-verzeichnis-der-verarbeitungstatigkeiten) |
| Information der Beschäftigten (Art. 13) | Kunde | Hinweis auf Archivierung, Frist, Zugriffsberechtigte, Umgang mit privaten E-Mails |
| Regelung privater E-Mail-Nutzung | Kunde | Verbot oder Trennung; sonst Mitbestimmung/Einwilligung prüfen |
| Betroffenenanfragen beantworten (1 Monat) | Kunde | Auskunftsfunktion, CSV, Export |
| Datenschutzerklärung der Oberfläche | Betreiber | `Views/Home/Privacy.cshtml` ist ein Platzhalter |
| Datenschutz-Folgenabschätzung (Art. 35) | Kunde | bei umfangreicher Überwachung von Beschäftigten prüfen |

## Anlage: Eintrag im Verzeichnis der Verarbeitungstätigkeiten

| | |
|---|---|
| Bezeichnung | E-Mail-Archivierung |
| Verantwortlicher | [Kunde] |
| Auftragsverarbeiter | [Dienstleister], AV-Vertrag vom [Datum] |
| Zwecke | Erfüllung gesetzlicher Aufbewahrungspflichten (§ 147 AO, § 257 HGB); Nachweisführung; Wiederherstellung |
| Rechtsgrundlage | Art. 6 Abs. 1 lit. c DSGVO |
| Kategorien betroffener Personen | Beschäftigte; Kommunikationspartner (Kunden, Lieferanten, Behörden, Sonstige) |
| Kategorien personenbezogener Daten | E-Mail-Adressen, Namen, Inhalte und Anhänge der E-Mails, Metadaten (Datum, Ordner); Benutzernamen und Zugriffsprotokolle der Archivbenutzer |
| Empfänger | Zugriffsberechtigte des Kunden laut Rollenkonzept; Dienstleister nur nach Freigabe (Support) oder zur Postfachverwaltung ohne Inhalte; Finanzverwaltung bei Betriebsprüfung |
| Drittlandübermittlung | keine / [S3-Anbieter und Standort des WORM-Speichers] |
| Löschfristen | E-Mails: [6/8/10] Jahre ab Ende des Kalenderjahres je Postfach (Anlage 1 der Verfahrensdokumentation); Audit-Log: [10] Jahre; Exportdateien: [30] Tage |
| TOM | siehe Anlage TOM |

## Anlage: Technische und organisatorische Maßnahmen (TOM)

Zutreffendes ankreuzen und ergänzen.

**Zugangs-/Zugriffskontrolle**
- [ ] Anmeldung mit Passwort (Mindestlänge [12]) und [2FA / OIDC/SSO]
- [ ] Rollen: Admin, Prüfer, Self-Manager, Benutzer; Zuweisung einzelner Postfächer
- [ ] Mandantentrennung in Anwendung (Abfragefilter) und Datenbank (Row Level Security mit eigener Datenbankrolle)
- [ ] Support-Zugriff des Dienstleisters nur nach Freigabe, mit Begründung, protokolliert
- [ ] Postfachverwaltung durch den Dienstleister ohne Zugriff auf Inhalte, protokolliert
- [ ] Zugriff auf Server, Datenbank, Backups auf [Personen] beschränkt

**Integrität und Unveränderbarkeit**
- [ ] Originale mit SHA-256 in Hash-Kette; Audit-Log hash-verkettet und nur anfügbar
- [ ] [WORM-Speicher (S3 Object Lock, Compliance-Modus)]
- [ ] [RFC-3161-Zeitstempel der Kettenanker]
- [ ] Integritätsprüfung [wöchentlich], Ergebnis gesichtet durch [wer]

**Verfügbarkeit**
- [ ] Datenbank-Backup [täglich], Aufbewahrung [x], Rücksicherungstest [jährlich]
- [ ] [Georedundanz des WORM-Speichers]

**Vertraulichkeit**
- [ ] HTTPS (Reverse-Proxy), aktuelle TLS-Konfiguration
- [ ] Verschlüsselung der Datenträger / Volumes
- [ ] Verschlüsselte Postfach-Zugangsdaten (`Security:EncryptAccountCredentials`)

**Löschung**
- [ ] Aufbewahrungsfristen je Postfach, automatische Löschung nach Ablauf
- [ ] Löschfrist Audit-Log [10 Jahre]
- [ ] Löschung von Exportdateien nach [30] Tagen

**Organisation**
- [ ] Verfahrensdokumentation (Vorlage `doc/Verfahrensdokumentation.md`)
- [ ] Prozess für Betroffenenanfragen (Auskunft, Einschränkung), Frist 1 Monat
- [ ] Jährliche Überprüfung der Berechtigungen
- [ ] Protokollsichtung [monatlich] durch [wer]
