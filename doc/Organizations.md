# Mandanten (Organisationen)

Dieser Fork macht Mail-Archiver mandantenfähig, damit ein Dienstleister die E-Mails
mehrerer Kundenfirmen in einer Installation archivieren kann. Jede **Organisation**
(Mandant) ist ein eigenes Archiv: Benutzer einer Organisation sehen nie Postfächer,
E-Mails, Anhänge, Protokolle, Statistiken oder Exporte einer anderen.

> Im Code heißen Mandanten `Organization`, weil `MailAccount.TenantId` bereits den
> Microsoft-365-Tenant eines Postfachs bezeichnet.

## Rollen

| Rolle | Wer | Darf |
|---|---|---|
| **Provider-Admin** | Mitarbeiter des Dienstleisters | Organisationen anlegen, bearbeiten, deaktivieren; Pakete und Kontingente; Postfächer der Organisationen verwalten; deren Admins anlegen; neue OIDC-Benutzer zuordnen; Zahlen sehen (Postfächer, Benutzer, E-Mails, Speicher), **keine Inhalte** |
| **Admin** | Admin einer Organisation | alles innerhalb der eigenen Organisation: Benutzer, Postfächer, Aufbewahrung, Legal Hold, Integritätsprüfung |
| **Prüfer** | z. B. Betriebsprüfer, Datenschutz | alle Postfächer der eigenen Organisation **lesen und exportieren**, Compliance-Seite einsehen, Prüfung starten; nichts ändern oder löschen |
| **Self-Manager / Benutzer** | wie im Original | nur zugewiesene Postfächer der eigenen Organisation |

Ein Provider-Admin gehört selbst einer Organisation an (typischerweise der eigenen Firma,
standardmäßig „Standard“) und ist dort ganz normaler Benutzer bzw. Admin. Die normale
Benutzerverwaltung zeigt ihm nur Benutzer dieser eigenen Organisation.

## Upgrade einer bestehenden Installation

Beim ersten Start mit diesem Fork:

* entsteht die Organisation **„Standard“** (Id 1, Kurzname `default`);
* wandern alle vorhandenen Postfächer, E-Mails, Anhänge, Protokolle und Benutzer dorthin;
* werden alle bisherigen Admins zusätzlich **Provider-Admins**;
* gilt für „Standard“ eine Aufbewahrung von **10 Jahren** – vorhandene E-Mails sind ab
  sofort bis Ende ihres Kalenderjahres + 10 Jahre gegen Löschen geschützt (siehe
  [Compliance](Compliance.md)). Wer das nicht möchte, stellt die Frist unter
  *Compliance* vor dem ersten Löschen auf den gewünschten Wert (0 = keine Pflicht).

## Organisation anlegen

*Organisationen → Neue Organisation*

* **Name**, **Kurzname** (Kleinbuchstaben/Ziffern/Bindestrich; erscheint in den
  Speicherpfaden des WORM-Speichers und ist danach unveränderlich)
* **Mail-Domains** (kommagetrennt): Wer sich per OIDC mit einer Adresse dieser Domains
  anmeldet, wird automatisch in dieser Organisation angelegt. Die Domains pflegt nur der
  Provider, damit kein Kunde fremde Domains für sich beanspruchen kann.
* **Aufbewahrung in Jahren** (Startwert; danach von der Organisation selbst verwaltet)
* optional **erster Admin** (Benutzername, E-Mail, Passwort ≥ 12 Zeichen)

Neue OIDC-Benutzer einer unbekannten Domain werden **ohne Organisation** angelegt und
sehen nichts. Der Provider ordnet sie unter *Organisationen → (Organisation) → Benutzer
ohne Organisation* zu.

Eine deaktivierte Organisation archiviert weiter, ihre Benutzer können sich aber nicht
mehr anmelden (auch nicht per API-Key).

## Pakete und Kontingente

*Organisationen → Pakete*

Ein **Paket** beschreibt, was der Dienstleister verkauft: **Speicher in GB** und die
**Anzahl der Postfächer** (beides optional, leer = unbegrenzt). Jede Organisation bucht ein
Paket; auf ihrer Seite (*Paket und Speicher*) kann der Provider außerdem **Einzelwerte**
eintragen, die das Paket für diese Organisation überschreiben (Sondervereinbarungen).

* **Speicherverbrauch** einer Organisation = durchsuchbare Kopie aller E-Mails samt Anhängen
  (wie im Original, nach jeder Synchronisation und nächtlich aktualisiert) **plus** die
  gespeicherten Originale (von der Datenbank sofort mitgezählt, gelöschte/gepurgte Originale
  zählen nicht mehr).
* **Postfach-Limit:** Die Datenbank lehnt jedes weitere Postfach ab (Formular, M365-Tenant-
  Import, CSV-Import, API), die Oberfläche zeigt eine verständliche Meldung.
* **Speicher-Kontingent:** ab `Quota:WarnPercent` (Standard 80 %) sehen die Admins der
  Organisation einen Hinweis, ab 100 % eine Warnung. Ob darüber hinaus die **Archivierung
  angehalten** wird, legt das Paket fest („Nur warnen“ oder „Archivierung anhalten“).
  Angehalten wird erst ab Kontingent + `Quota:BlockGracePercent` (Standard 10 %); die
  Synchronisation läuft von selbst weiter, sobald das Paket erweitert wurde, und holt nach,
  was noch auf dem Mailserver liegt. **Für aufbewahrungspflichtige Postfächer ist „Nur
  warnen“ die sicherere Einstellung** – eine Lücke im Archiv wiegt schwerer als ein
  überzogenes Kontingent.
* **Verbrauch (CSV):** Die Anwendung schreibt stündlich (`Quota:SnapshotIntervalMinutes`)
  den Tageswert jeder Organisation. *Organisationen → Verbrauch (CSV)* exportiert pro Monat
  Spitze, Durchschnitt und letzten Wert in GB sowie Postfächer und E-Mails – als Grundlage
  für die Abrechnung.

### Wer sieht welchen Speicher?

| Wer | sieht |
|---|---|
| Provider-Admin | Verbrauch jeder Organisation und jedes Postfachs darin, Kontingent, Paket |
| Admin / Prüfer einer Organisation | Verbrauch der eigenen Organisation und ihrer Postfächer, Paket und Kontingent (Compliance-Seite, Hinweisbanner) |
| Benutzer | nur den Verbrauch der ihm zugewiesenen Postfächer; kein Paket, kein Banner |

Niemand sieht die Größe der gesamten Datenbank außer dem Provider.

## Postfächer verwalten (Verwaltungsmodus)

*Organisationen → Postfächer verwalten* (oder auf der Seite der Organisation)

Der Provider-Admin kann Postfächer einer Organisation **ohne deren Freigabe** anlegen,
bearbeiten, synchronisieren und importieren – das ist Teil der Dienstleistung. Dabei gilt:

* **Keine Inhalte.** E-Mails, Suche, Exporte, Dashboard, Benutzer, Audit-Log und
  Compliance-Seite der Organisation sind im Verwaltungsmodus gesperrt
  (`Services/Organizations/ProviderManagementFilter.cs`); auch Konto-Exporte und das
  Verschieben von E-Mails zwischen Postfächern.
* **Alles protokolliert.** Beginn, Ende und jede Änderung landen im hash-verketteten
  Audit-Log der Organisation.
* Ein blauer Balken zeigt den aktiven Verwaltungsmodus; *Verwaltung beenden* schließt ihn.

Für Zugriff auf Inhalte bleibt es beim Support-Zugriff, den die Organisation freigeben muss.

## Support-Zugriff des Dienstleisters

Ein Provider-Admin kann das Archiv einer Organisation nur öffnen, wenn deren eigene
Admins das unter *Compliance → Support-Zugriff durch den Dienstleister erlauben*
freigegeben haben. Beim Öffnen ist eine **Begründung** Pflicht (z. B. Ticketnummer).

* Beginn und Ende des Zugriffs samt Begründung landen im **Audit-Log der Organisation**
  (hash-verkettet, siehe [Compliance](Compliance.md)) – der Kunde sieht also, wer wann
  und warum hineingeschaut hat.
* Alle Aktionen während des Zugriffs werden ebenfalls im Audit-Log des Kunden protokolliert.
* Ein gelber Balken zeigt den aktiven Zugriff an; *Support-Zugriff beenden* schließt ihn.
* Der Provider-Admin handelt dabei mit seinen eigenen Rechten (in der Regel Admin).

Das entspricht dem Modell einer Auftragsverarbeitung (Art. 28 DSGVO): Der Dienstleister
greift nur auf Weisung und nachvollziehbar zu. Halten Sie Freigabe und Protokollierung im
AV-Vertrag fest.

## Wie die Trennung technisch funktioniert

1. **Zuordnung durch die Datenbank.** E-Mails und Anhänge erhalten ihre `OrganizationId`
   per Trigger vom Postfach bzw. von der E-Mail – kein Schreibweg kann eine E-Mail einer
   falschen Organisation zuordnen. Ein Postfach mit E-Mails kann die Organisation nicht
   wechseln; Benutzer können nur Postfächer der eigenen Organisation zugewiesen bekommen.
2. **Globale Abfragefilter (EF Core).** Jede Datenbankabfrage einer angemeldeten Person
   ist automatisch auf ihre Organisation beschränkt
   (`Data/MailArchiverDbContext.Organizations.cs`). Hintergrunddienste (Synchronisation,
   Wartung) laufen ohne Benutzer und sehen alle Organisationen; sie arbeiten nur mit
   Datensätzen, die sie per Id erhalten haben.
3. **Row Level Security (PostgreSQL).** Zusätzlich setzt die Anwendung bei jeder
   Verbindung `mail_archiver.org_id`; Policies auf allen Mandantentabellen lassen nur
   Zeilen dieser Organisation sichtbar. Das schützt auch Stellen mit direktem SQL.
   **Wichtig:** PostgreSQL wendet Row Level Security nicht auf Superuser an. Das
   docker-compose des Originals verbindet sich als Superuser – damit diese zweite Ebene
   greift, richten Sie eine eigene Rolle ein:

   ```bash
   docker compose exec -T postgres psql -U mailuser -d MailArchiver \
       -v app_password="'ein-langes-zufälliges-passwort'" -f - < tools/create-app-role.sql
   ```

   und stellen die Verbindung um:
   `ConnectionStrings__DefaultConnection=Host=postgres;Database=MailArchiver;Username=mailarchiver_app;Password=...`

   Die Rolle wird Eigentümerin des Schemas (Migrationen laufen weiter), hat aber weder
   `SUPERUSER` noch `BYPASSRLS`. Erweiterungen (pgcrypto, citext) müssen vorher existieren;
   die Anwendung legt sie beim ersten Start als Superuser an.
4. **Getrennte Deduplizierung.** Anhänge werden nur innerhalb einer Organisation
   dedupliziert. Sonst könnte ein Kunde über die Speicherbelegung erkennen, ob ein anderer
   dieselbe Datei besitzt.
5. **Getrennte Statistiken.** Dashboard-Zahlen werden pro Organisation berechnet und
   zwischengespeichert; statt der Größe der ganzen Datenbank sieht ein Kunde den Speicher
   seiner eigenen Postfächer.

## Grenzen

* Alle Organisationen teilen sich eine Datenbank und eine Anwendung. Wer Betriebssystem
  oder Datenbank als Superuser kontrolliert, kann alles lesen – das gilt für jede
  gemeinsam betriebene Lösung. Gegen nachträgliche Veränderung schützen die Hash-Ketten,
  Anker und der WORM-Speicher (siehe [Compliance](Compliance.md)), nicht gegen Lesen.
* Eine Organisation kann nicht gelöscht werden, solange Daten darin liegen (Fremdschlüssel).
  Daten eines Kunden werden über die Aufbewahrungsfristen gelöscht.
* Der Support-Zugriff ist kein Nur-Lese-Zugriff; er ist protokolliert, nicht eingeschränkt.
* Benutzernamen und E-Mail-Adressen von Benutzern sind instanzweit eindeutig. Ein Admin
  erkennt beim Anlegen also, dass ein Name in irgendeiner Organisation bereits vergeben ist
  (nicht in welcher).
* Hintergrund-Jobs (Exporte, Wiederherstellungen, Importe, Löschungen) laufen ohne
  Benutzerkontext. Sie tragen die Organisation, in der sie gestartet wurden; alle IDs
  werden beim Einreihen gegen die Organisation der anfragenden Person geprüft, und
  Status-, Download- und Abbruchseiten zeigen nur Jobs der eigenen Organisation.
