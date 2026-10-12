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
| **Provider-Admin** | Mitarbeiter des Dienstleisters | Organisationen anlegen, bearbeiten, deaktivieren; deren Admins anlegen; neue OIDC-Benutzer zuordnen; Zahlen sehen (Postfächer, Benutzer, E-Mails, Speicher), **keine Inhalte** |
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
