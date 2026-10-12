# Vorlage: Verfahrensdokumentation E-Mail-Archivierung

Diese Vorlage folgt dem Aufbau der GoBD (Rz. 151 ff.) und der Muster-Verfahrensdokumentation
des AWV. Sie ist für eine Organisation (einen Mandanten) gedacht. Ersetzen Sie alle
Angaben in `[eckigen Klammern]`, streichen Sie Unzutreffendes und lassen Sie das Ergebnis
von Ihrer Steuerberatung prüfen. **Keine Rechtsberatung.**

---

## 1. Allgemeine Beschreibung

| | |
|---|---|
| Unternehmen | [Name, Anschrift] |
| Verantwortlich für das Verfahren | [Name, Funktion] |
| Dienstleister (Betrieb) | [Name des Dienstleisters, AV-Vertrag vom …] |
| Gegenstand | Archivierung aller ein- und ausgehenden E-Mails der Postfächer laut Anlage 1, einschließlich Anhänge |
| Ziel | Erfüllung der Aufbewahrungspflichten nach § 147 AO und § 257 HGB, Datenzugriff der Finanzverwaltung (§ 147 Abs. 6 AO), Nachweisführung |
| Gültig ab | [Datum] |
| Version | [x.y], Änderungshistorie siehe Abschnitt 8 |

## 2. Anwenderdokumentation

### 2.1 Erfasste Postfächer und Abruf
* Postfächer: siehe Anlage 1 (Name, Adresse, Anbieter, Aufbewahrungsfrist).
* Abruf: automatisch per [IMAP / Microsoft Graph] alle [N] Minuten, alle Ordner außer [ausgeschlossene Ordner].
* Archiviert wird jede E-Mail **im Original** (Rohdaten wie vom Server geliefert) zusammen
  mit den daraus gelesenen Feldern zur Suche.
* Altbestand vor [Datum der Umstellung] wurde importiert/versiegelt: [Beschreibung].

### 2.2 Aufbewahrungsfristen
| Kategorie | Frist | Postfächer |
|---|---|---|
| Handels- und Geschäftsbriefe | 6 Jahre | [ … ] |
| Buchungsbelege (z. B. Rechnungen per E-Mail) | 8 Jahre | [ … ] |
| Sonstige / Standard | [10] Jahre | alle übrigen |
| keine Aufbewahrungspflicht (privat) | 0 | [ … ] |

Fristbeginn: Ende des Kalenderjahres der E-Mail. Nach Ablauf werden E-Mails [automatisch
täglich / auf Anweisung von …] gelöscht, sofern kein Legal Hold besteht.

### 2.3 Zugriff und Rollen
| Rolle | Personen | Rechte |
|---|---|---|
| Admin | [ … ] | Verwaltung, Fristen, Legal Hold |
| Prüfer | [Betriebsprüfung bei Bedarf] | Lesen und Export aller Postfächer |
| Benutzer | [ … ] | Lesen zugewiesener Postfächer |
| Dienstleister (Support) | [ … ] | nur nach Freigabe, protokolliert |

Anmeldung: [Passwort + 2FA / OIDC über …].

### 2.4 Datenzugriff der Finanzverwaltung
* Z1 (unmittelbarer Zugriff): Prüfer-Benutzer mit Lesezugriff.
* Z2 (mittelbarer Zugriff): Auswertung durch [Name] nach Vorgabe der Prüfung.
* Z3 (Datenträgerüberlassung): Audit-Export (GDPdU-Beschreibungsstandard) und Originale (.eml).

## 3. Technische Systemdokumentation

* Software: Mail-Archiver (Fork mit Mandanten und Revisionssicherheit), Version [ … ],
  Quellcode: [Repository-URL, Commit].
* Betrieb: [Rechenzentrum/Standort], Container auf [Host], PostgreSQL [Version].
* Speicher der Originale: [Datenbank / S3 Object Lock, Anbieter, Bucket, Modus COMPLIANCE].
* Zeitstempeldienst: [Anbieter / keiner].
* Mandantentrennung: siehe `doc/Organizations.md`; Datenbankrolle ohne Superuser: [ja/nein].

## 4. Betriebsdokumentation

* Überwachung der Synchronisation: [wer, wie oft, wie werden Fehler behandelt].
* Integritätsprüfung: automatisch [wöchentlich]; Kontrolle des Ergebnisses durch [wer, wann];
  Vorgehen bei Befund: [Eskalation, Ursachenanalyse, Dokumentation].
* Anker der Hash-Ketten: täglich, im WORM-Speicher [und mit Zeitstempel].
* Datensicherung: [Datenbank-Backup Häufigkeit, Aufbewahrung, Ort]; Rücksicherungstest [Intervall].
* Updates der Software: [Verfahren, Test, Freigabe].
* Notfall: [Wiederanlauf, Ansprechpartner].

## 5. Internes Kontrollsystem (IKS)

* Unveränderbarkeit: Originale und Audit-Log nur anfügbar und hash-verkettet;
  Prüfung mit unabhängigem Skript (`tools/verify_chain.py`) [jährlich durch …].
* Löschung vor Fristablauf technisch ausgeschlossen (Datenbank-Trigger, WORM-Sperre).
* Legal Hold: Setzen/Aufheben nur durch [Rolle], protokolliert.
* Zugriffe: Protokoll (Audit-Log) wird [monatlich] durch [wer] gesichtet.
* Support-Zugriffe des Dienstleisters: nur mit Freigabe, Begründung, Protokoll; Kontrolle durch [wer].
* Berechtigungen: jährliche Überprüfung der Benutzer und Rollen durch [wer].

## 6. Datenschutz

* Rechtsgrundlage der Archivierung: Art. 6 Abs. 1 lit. c DSGVO i. V. m. § 147 AO / § 257 HGB.
* Löschkonzept: Löschung nach Fristablauf (Abschnitt 2.2); Löschnachweis in der Hash-Kette.
* Auftragsverarbeitung: AV-Vertrag mit [Dienstleister] vom [Datum].
* Betroffenenrechte: Auskunft über [Verfahren]; Löschbegehren werden gegen
  Aufbewahrungspflichten abgewogen (Art. 17 Abs. 3 lit. b DSGVO).

## 7. Anlagen

1. Liste der Postfächer mit Fristen
2. Rollen- und Berechtigungskonzept
3. Konfiguration (ohne Geheimnisse): `ArchiveStorage`, `Compliance`
4. Letzte Integritätsprüfung (Ausdruck der Compliance-Seite) und Kettenexport

## 8. Änderungshistorie

| Version | Datum | Änderung | Freigabe |
|---|---|---|---|
| 1.0 | [ … ] | Erstfassung | [ … ] |
