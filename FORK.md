# Fork: Mandanten und revisionssichere Archivierung

Dieser Fork von [s1t5/mail-archiver](https://github.com/s1t5/mail-archiver) ergänzt:

* **Mandanten (Organisationen)** für Dienstleister: strikte Trennung der Kundendaten
  (Datenbank-Trigger, EF-Abfragefilter, PostgreSQL Row Level Security), eigene Admins je
  Kunde, Prüfer-Rolle, protokollierter Support-Zugriff nur mit Freigabe des Kunden.
  → [doc/Organizations.md](doc/Organizations.md)
* **Revisionssichere Archivierung (GoBD)**: Original jeder E-Mail byte-genau mit SHA-256
  in einer Hash-Kette, Aufbewahrungsfristen nach § 147 AO mit technischer Löschsperre,
  Legal Hold, optional WORM-Speicher (S3 Object Lock), hash-verkettetes Audit-Log,
  tägliche Anker (optional RFC-3161-Zeitstempel), Integritätsprüfung, unabhängiges
  Prüfskript. → [doc/Compliance.md](doc/Compliance.md),
  [Vorlage Verfahrensdokumentation](doc/Verfahrensdokumentation.md)

## Upstream-Updates einspielen

Die Erweiterung liegt weitgehend in eigenen Dateien (`*/Organizations/*`, `*/Compliance/*`,
`Migrations/*MigrateFork_*`, `Resources/ComplianceResource*`, eigene Controller und Views).
In Dateien des Originals sind die Eingriffe klein und kommentiert.

```bash
git remote add upstream https://github.com/s1t5/mail-archiver.git   # einmalig
git fetch upstream
git merge upstream/main
```

Worauf beim Mergen zu achten ist:

* **Neue Migrationen des Originals** laufen nach den Fork-Migrationen (spätere Zeitstempel).
  Ersetzt das Original die Funktion `mail_archiver.prevent_locked_email_changes()`, müssen
  dort die Spalten `LegalHold` und `RetainUntil` wieder ausgenommen werden
  (siehe `MigrateFork_1`).
* **Neue Tabellen mit Mandantendaten** brauchen eine `OrganizationId`, einen Abfragefilter
  in `Data/MailArchiverDbContext.Organizations.cs` und eine RLS-Policy.
* **Neue Stellen mit direktem SQL** (`NpgsqlCommand`, `FromSql…`, `ExecuteSql…`) umgehen
  die Abfragefilter: auf die Organisation einschränken und
  `OrganizationConnectionInterceptor.ApplyScopeAsync` verwenden.
* **Neue Schreibwege für E-Mails** müssen `IOriginalArchiveService.AttachAsync` aufrufen
  (sonst werden die E-Mails nur nachträglich versiegelt).
* **Neue Löschwege** werden von der Datenbank geprüft; die Oberfläche sollte `MA001`/`MA002`
  über `RetentionGuard.Describe` verständlich melden.
* Danach: `dotnet test` (die Tests unter `tests/MailArchiver.Tests/Compliance` prüfen
  Trennung, Ketten, Fristen und Manipulationserkennung).

Lizenz: GPL-3.0 wie das Original.
