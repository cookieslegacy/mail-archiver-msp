# Revisionssichere Archivierung (GoBD)

Dieser Fork ergänzt Mail-Archiver um die technischen Bausteine einer revisionssicheren
E-Mail-Archivierung nach den GoBD (Grundsätze zur ordnungsmäßigen Führung und
Aufbewahrung von Büchern, Aufzeichnungen und Unterlagen in elektronischer Form):

| Anforderung (GoBD) | Umsetzung |
|---|---|
| **Vollständigkeit** | automatischer Abruf aller Postfächer (wie im Original); Integritätsprüfung meldet Lücken in der Kette |
| **Unveränderbarkeit** | Original jeder E-Mail byte-genau mit SHA-256; nur anfügbare Tabellen; optional WORM-Speicher (S3 Object Lock) |
| **Nachvollziehbarkeit / Prüfbarkeit** | Hash-Kette über alle Originale und über das Audit-Log; Anker (WORM + RFC-3161-Zeitstempel); CSV-Export der Kette und unabhängiges Prüfskript |
| **Aufbewahrungsfristen** | Frist pro Organisation/Postfach, Beginn mit Ende des Kalenderjahres (§ 147 Abs. 4 AO); Löschen vorher technisch ausgeschlossen |
| **Löschen nach Fristablauf (DSGVO)** | automatisch oder per Knopf; Löschnachweis bleibt in der Kette |
| **Legal Hold** | pro E-Mail, Postfach oder Organisation; blockiert jedes Löschen, auch im WORM-Speicher |
| **Zugriffsprotokoll** | Audit-Log nur anfügbar und hash-verkettet |
| **Datenzugriff der Finanzverwaltung (Z1–Z3)** | Prüfer-Rolle mit Lesezugriff (Z1/Z2); GDPdU-Export des Original-Projekts und Download jedes Originals (Z3) |

> **Keine Rechtsberatung.** Software allein ist nicht „GoBD-zertifiziert“ – geprüft wird
> immer das ganze Verfahren. Sie brauchen zusätzlich eine
> [Verfahrensdokumentation](Verfahrensdokumentation.md) und organisatorische Regeln;
> wer mit Revisionssicherheit wirbt, sollte das Verfahren z. B. nach IDW PS 880 prüfen lassen.

## 1. Originale und Hash-Kette

Jede neu archivierte E-Mail wird zusätzlich **genau so gespeichert, wie sie ankam**:

| Quelle | Original |
|---|---|
| IMAP | Rohdaten vom Server (`BODY.PEEK[]`), byte-genau |
| Microsoft 365 (Graph) | MIME-Inhalt (`/messages/{id}/$value`) |
| EML-Import | die importierte Datei |
| MBox-Import | die Nachricht aus der MBox-Datei (ohne „From “-Trennzeile) |
| Sonderfälle | von MimeKit neu serialisiert (Quelle „serialized“), z. B. nach Reparatur defekter Header |

Original, E-Mail und Anhänge werden in **einer Transaktion** gespeichert. Gelingt das
Original nicht (z. B. WORM-Speicher nicht erreichbar), gilt die E-Mail als fehlgeschlagen
und wird beim nächsten Abruf erneut versucht – es entsteht keine Archiv-E-Mail ohne Original.

Die Tabelle `ArchivedEmailOriginals` ist **nur anfügbar**. Die Datenbank

* prüft beim Einfügen den SHA-256 der Bytes,
* verknüpft jeden Eintrag mit dem vorherigen der Organisation
  (`ChainHash = SHA-256(Eintrag + PrevChainHash)`, unter Sperre, also lückenlos),
* verweigert `DELETE`, `TRUNCATE` und jede Änderung außer: Bytes in den WORM-Speicher
  verschieben, Bytes nach Fristablauf löschen, Frist verlängern.

Wird eine E-Mail gelöscht (nach Fristablauf), verschwinden die **Bytes** ihres Originals;
der **Ketteneintrag bleibt** mit Zeitpunkt und Grund als Löschnachweis.

**Altbestand:** E-Mails, die vor dem Upgrade archiviert wurden, haben kein Original. Sie
werden automatisch **versiegelt**: Ein SHA-256 über die gespeicherten Felder
(Betreff, Adressen, Datum, Header, Text, HTML, Anhang-Hashes) wird in die Kette
aufgenommen. Das beweist, dass sich diese Felder nach dem Versiegeln nicht mehr geändert
haben – nicht, dass sie dem ursprünglichen Original entsprechen.

In der E-Mail-Ansicht zeigt der Kasten **Archivnachweis** Ketteneintrag, SHA-256,
Frist und Legal Hold; *Original (.eml)* lädt das Original herunter (der Download wird
gegen den SHA-256 geprüft und protokolliert).

## 2. Aufbewahrungsfristen

Die Frist beginnt mit dem **Schluss des Kalenderjahres** der E-Mail (§ 147 Abs. 4 AO) und
endet am 01.01. 00:00 UTC des Jahres nach *Jahr + Frist*. Eine E-Mail vom März 2026 mit
10 Jahren ist bis 31.12.2036 geschützt. Maßgeblich ist das spätere von Sende- und
Empfangsdatum (ein falsches Date-Header kann die Frist nur verlängern).

Übliche Fristen (§ 147 AO / § 257 HGB, Stand 2025): Handels- und Geschäftsbriefe
**6 Jahre**, Buchungsbelege (z. B. Rechnungen) **8 Jahre**, Bücher und Aufzeichnungen
**10 Jahre**. Da E-Mails oft Buchungsbelege enthalten, ist 10 Jahre die sichere
Voreinstellung. Prüfen Sie die Fristen mit Ihrer Steuerberatung.

* Frist pro **Organisation** (*Compliance → Aufbewahrung*), abweichend pro **Postfach**
  (z. B. 0 für ein privates Postfach, 6 für eine reine Korrespondenzadresse).
* Die Datenbank speichert die Frist jeder E-Mail beim Archivieren (`RetainUntil`).
  Sie kann **verlängert, aber nie verkürzt** werden. Eine geänderte Vorgabe gilt nur für
  neue E-Mails.
* **Jeder** Löschweg wird von der Datenbank geprüft: manuelles Löschen, Massenlöschung,
  lokale Aufbewahrungsregel des Postfachs, Löschen eines Postfachs (Kaskade), direktes SQL.
  Vor Fristablauf antwortet die Datenbank mit `MA001`, bei Legal Hold mit `MA002`; die
  Oberfläche zeigt eine verständliche Meldung. Ein Postfach mit geschützten E-Mails kann
  nur deaktiviert werden.
* Nach Ablauf: *Compliance → abgelaufene E-Mails jetzt löschen* oder automatisch
  (*E-Mails nach Ablauf automatisch löschen*, täglich). Das ist für personenbezogene Daten
  ohne Aufbewahrungspflicht nach DSGVO geboten.

Der bestehende Schalter `DeletionPolicy:DeletionAllowed` des Originals bleibt erhalten
und wirkt zusätzlich.

## 3. Legal Hold

Ein Legal Hold (z. B. wegen eines Rechtsstreits oder einer Prüfung) verhindert jedes
Löschen, auch nach Fristablauf:

* **E-Mail**: Knopf *Legal Hold setzen* im Archivnachweis,
* **Postfach**: *Compliance → Aufbewahrung je Postfach*,
* **Organisation**: *Compliance → Legal Hold für die gesamte Organisation* (mit Grund/Aktenzeichen).

Setzen und Aufheben werden im Audit-Log protokolliert. Mit WORM-Speicher wird der Hold
zusätzlich als **S3 Legal Hold** auf die Objekte übertragen.

## 4. WORM-Speicher (S3 Object Lock)

Ohne WORM liegen die Originale in der Datenbank. Gegen Datenbank-Administratoren schützt
dann „nur“ die Nachweisbarkeit (Kette + Anker). Mit WORM-Speicher werden die Originale
zusätzlich in einem S3-Bucket mit **Object Lock im Compliance-Modus** abgelegt: Bis zum
Fristende kann **niemand** – auch nicht der Eigentümer des Buckets oder der Anbieter
dieser Software – die Objekte löschen oder überschreiben.

Geeignet sind S3-kompatible Speicher mit Object Lock, z. B. AWS S3, Wasabi, Backblaze B2,
IONOS, OVH, Hetzner Object Storage, Cloudian, NetApp StorageGRID, Ceph RGW.
Den Bucket **mit aktiviertem Object Lock anlegen** (lässt sich später meist nicht mehr
einschalten); Versionierung ist dann automatisch aktiv.

```json
"ArchiveStorage": {
  "Mode": "S3",
  "KeepDatabaseCopy": false,
  "S3": {
    "ServiceUrl": "https://s3.eu-central-1.example.com",
    "Region": "eu-central-1",
    "Bucket": "mailarchiv-worm",
    "AccessKey": "...",
    "SecretKey": "...",
    "ForcePathStyle": true,
    "LockMode": "COMPLIANCE"
  }
}
```

* Schlüssel: `<kurzname>/originals/<jahr>/<sha[0..2]>/<sha256>.eml` (inhaltsadressiert;
  dieselbe E-Mail in zwei Postfächern derselben Organisation belegt ein Objekt).
* Die Sperrfrist des Objekts ist die Aufbewahrungsfrist der E-Mail beim Archivieren.
  Ein Legal Hold wird auf die Objekte übertragen; eine nachträglich verlängerte Frist
  (`RetainUntil`) wird derzeit nur in der Datenbank verlängert, nicht am Objekt.
* `KeepDatabaseCopy: true` hält zusätzlich eine Kopie in der Datenbank (schnellere
  Downloads, doppelter Speicher).
* Beim Start prüft die Anwendung, ob der Bucket Object Lock hat, und meldet Fehler im Log
  und auf der Compliance-Seite.
* `GOVERNANCE` statt `COMPLIANCE` nur zum Testen – dort können privilegierte Nutzer die
  Sperre aufheben.
* Bestehende Originale aus der Datenbank werden nicht automatisch verschoben.

Der Zugangsschlüssel der Anwendung braucht `s3:PutObject`, `s3:GetObject`,
`s3:GetObjectVersion`, `s3:PutObjectRetention`, `s3:PutObjectLegalHold`,
`s3:GetObjectRetention`, `s3:GetObjectLegalHold`, `s3:GetBucketObjectLockConfiguration`,
`s3:DeleteObjectVersion` (für Löschen nach Fristablauf).

## 5. Manipulationssicheres Audit-Log

Das Zugriffsprotokoll (*Protokoll*) ist jetzt **nur anfügbar** und pro Organisation
**hash-verkettet** – wie die Originale. Einträge können nicht geändert oder gelöscht
werden; ein Entfernen würde die Kette brechen. Neu protokolliert werden u. a.
Organisationsänderungen, Support-Zugriffe, Legal Hold, Integritätsprüfungen und Löschungen
nach Fristablauf.

## 6. Anker und Zeitstempel

Eine Kette allein beweist nur innere Konsistenz: Wer die gesamte Datenbank kontrolliert,
könnte die Kette vollständig neu berechnen. Deshalb wird der aktuelle Stand beider Ketten
**täglich verankert**:

* als kleines JSON-Dokument im **WORM-Speicher** (unter `<kurzname>/anchors/...`,
  gesperrt für Frist + 1 Jahr), und
* optional mit einem **qualifizierten Zeitstempel nach RFC 3161**
  (`Compliance:TimestampAuthorityUrl`, z. B. eines Vertrauensdiensteanbieters nach eIDAS
  oder zum Testen `https://freetsa.org/tsr`).

Eine nachträglich umgeschriebene Kette passt nicht mehr zu den Ankern – die Prüfung
meldet das. Der Test `Verification_DetectsARewrittenChainThroughItsAnchor` zeigt genau
diesen Fall.

## 7. Integritätsprüfung

Wöchentlich (Einstellung `Compliance:VerifyIntervalHours`) und jederzeit über
*Compliance → Integrität jetzt prüfen* wird pro Organisation geprüft:

* jede Verknüpfung der Originale-Kette (Reihenfolge, Vorgänger, Hash – unabhängig in C#
  nachgerechnet, nicht mit den Datenbankfunktionen),
* jedes Original gegen seinen SHA-256 (Datenbank und WORM-Objekt),
* jedes Siegel des Altbestands gegen die gespeicherten Felder,
* jede Verknüpfung des Audit-Logs,
* jeder Anker gegen die Ketten, gegen seine WORM-Kopie und seinen Zeitstempel.

Das Ergebnis erscheint auf der Compliance-Seite (mit Detailbericht) und im Audit-Log.

### Unabhängige Prüfung durch Dritte

*Compliance → Kette exportieren (CSV)* liefert alle Ketteneinträge. Ein Prüfer kann sie
ohne diese Software nachrechnen:

```bash
python3 tools/verify_chain.py originals-chain.csv --emails ./originale --anchor anker.json
```

Das Skript nutzt nur die Python-Standardbibliothek; die kanonische Form steht im Kopf des
Skripts.

## 8. Konfiguration

```json
"Compliance": {
  "VerifyIntervalHours": 168,
  "AnchorIntervalHours": 24,
  "TimestampAuthorityUrl": "",
  "PurgeIntervalHours": 24,
  "BatchSize": 1000,
  "SealLegacyEmails": true
}
```

## 9. Was Sie zusätzlich tun müssen

1. **Verfahrensdokumentation** erstellen ([Vorlage](Verfahrensdokumentation.md)).
2. **WORM-Speicher** einrichten (sonst ist der Schutz gegen Administratoren nur nachweisend).
3. **Datenbankrolle ohne Superuser** einrichten (`tools/create-app-role.sql`), damit Row
   Level Security greift.
4. **Zeitstempeldienst** konfigurieren, wenn Zeitpunkte gegenüber Dritten belegbar sein müssen.
5. **Datensicherung**: Datenbank-Backups (das WORM-Objekt ersetzt kein Backup der
   Metadaten) und regelmäßige Rücksicherungstests.
6. **Zugriffe auf Server und Datenbank** beschränken und protokollieren.
7. **Fristen** mit der Steuerberatung festlegen; Postfächer ohne Aufbewahrungspflicht
   (z. B. privat) auf 0 setzen.
8. **Integritätsprüfungen** überwachen (Status auf der Compliance-Seite / Log).

## Technische Referenz

| Baustein | Ort |
|---|---|
| Datenbank: Organisationen, RLS, Trigger | `Migrations/20261012120000_MigrateFork_1.cs` |
| Datenbank: Originale, Kette, Prüfläufe, Anker | `Migrations/20261012120100_MigrateFork_2.cs` |
| Datenbank: Fristen, Legal Hold, Löschsperre | `Migrations/20261012120200_MigrateFork_3.cs` |
| Datenbank: Audit-Kette | `Migrations/20261012120300_MigrateFork_4.cs` |
| Originale speichern / lesen / löschen | `Services/Compliance/OriginalArchiveService.cs` |
| Kanonische Formen (C#) | `Services/Compliance/IntegrityChain.cs` |
| Prüfung | `Services/Compliance/IntegrityVerificationService.cs` |
| Anker, RFC 3161 | `Services/Compliance/AnchorService.cs` |
| WORM (S3) | `Services/Compliance/WormStore.cs` |
| Fristen, Altbestand, Löschen | `Services/Compliance/RetentionService.cs` |
| Hintergrunddienst | `Services/Compliance/ComplianceBackgroundService.cs` |
| Tests | `tests/MailArchiver.Tests/Compliance/` |
