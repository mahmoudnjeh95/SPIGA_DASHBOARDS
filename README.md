# SPIGA_DASHBOARDS

Production tracking for the SPIGA pasta plant: the **SPIGA Dashboards** kiosk
(packaging line slides, TV screen) and, on the development branch, **SPIGA
Suivi Production**, the application of the cahier des charges of 12 November
2026, which becomes the main view and contains the dashboard.

## Structure

| Project | What it is |
|---|---|
| `src/SpigaDashboards.App` | The Windows application (WPF, .NET 8). One window (`ShellWindow`): the SPIGA Suivi Production screens, and the classic dashboard (`DashboardView`) shown inside it under Production › Tableau de bord, beside the menu. `--kiosk` shows the dashboard alone (`KioskWindow`) for TV / hall PCs. |
| `src/SpigaDashboards.Production` | Production database access (Stiavelli `stats`, read only), KPI / TRS calculation, team roster, station settings. Moved out of the app unchanged, so the dashboard and the new screens compute the same figures. |
| `src/Spiga.Core` | Domain rules with no UI and no SQL: modules and permissions, users and roles, data scope, authentication and lockout, waste entry workflow, audit trail. |
| `src/Spiga.Data` | The application database (separate from `stats`): SQLite on a dev PC, SQL Server on site. Versioned migrations run at start-up. |
| `src/Spiga.UI` | The screens, as Razor components. The same components run in the desktop app (BlazorWebView) and in a browser. |
| `src/Spiga.Web` | Browser host (Blazor Server). Development and demonstrations today; the deployment path if the company moves to web applications. |
| `tests/Spiga.Tests` | Tests of the security and waste rules against a real SQLite database. |

Moving to the web changes the host (`Spiga.Web`), not the screens.

## Run

```powershell
dotnet build SpigaDashboards.sln
dotnet run --project src/SpigaDashboards.App            # desktop app, sign-in first
dotnet run --project src/SpigaDashboards.App -- --kiosk # classic dashboard only, no sign-in (TV PCs)
dotnet run --project src/Spiga.Web                      # same screens in a browser
dotnet test tests/Spiga.Tests
```

On the first start, with an empty application database, the application
asks for the first administrator account. On a dev PC the application
database is `database/spiga_suivi.db` (created automatically, not in git).
On site, IT runs `database/spiga_suivi.sql` once on the SQL Server (the
database, and a login for the PCs that only reads and writes it), then
start menu « Mettre à jour la base SPIGA (informatique) »
(`SpigaDashboards.App.exe --update-database`) with an administrator login,
which creates the tables. IT runs it again for each version that changes
the tables; until then the PCs say the base must be updated. (A login still
allowed to change the tables, as on sites installed before 2.1, updates
the base itself; `spiga_suivi.sql` takes that right back.) Browser host:
`--update-database` with `Spiga:AppDatabase:AdminSqlServer`.

The SQL password of the PCs is encrypted for the Windows account that
saved it (DPAPI user scope); a password saved by an older version, readable
by any account of the PC, is re-encrypted at the next start. Windows
authentication is recommended: no password is stored at all.

The desktop shell needs the Microsoft Edge WebView2 runtime (present on
Windows 10 and 11).

## Databases

| Database | Holds | Written by the application |
|---|---|---|
| `stats` (Stiavelli, production) | Live machine data (`produzione`, `lotto`, status log) and the reference tables IT installs with `database/stiavelli_compat.sql` (`Machines`, `Products`, `Shifts`, `Employees`) | **Never** (from 2.1) |
| `spiga_suivi` (application) | Accounts, roles, platforms, waste entries, audit trail; planning of the stops (`Plan*`), the dashboard's note (with its author), machine display names, the settings shared by the PCs | Yes, through versioned migrations (`src/Spiga.Data/Migrations`) |

Every PC needs both, kiosk / TV PCs included (planning, note and shared
settings).

### Updating from 2.0, PC by PC

Up to version 2.0 the application wrote its planning, note, machine names
and shared settings in `stats`. The PCs do not need to be updated together
(`PlantDataLocation`):

1. IT runs `spiga_suivi.sql` again, then « Mettre à jour la base SPIGA
   (informatique) » on one PC (answer *Non* to the transfer question).
2. The PCs are updated one by one, at any pace. Meanwhile the 2.1 PCs keep
   the planning, note, machine names and shared settings in `stats`, like
   the 2.0 PCs, so every PC sees the same (the audit trail is in
   `spiga_suivi`). Utilisateurs et rôles shows a warning: the 2.0 PCs ignore
   platforms (an account limited by platform sees all its team's machines
   there, and an account edited from one loses its platforms) — manage the
   accounts from 2.1 PCs, and give platforms only to accounts that use
   updated PCs.
3. When the last PC (TV screens included) runs 2.1, IT runs « Mettre à
   jour la base SPIGA » again and answers *Oui*: writes pause for about 90 s
   (the screens keep showing), everything is copied to `spiga_suivi` in one
   transaction (ids kept; production order numbers given twice renumbered
   `-2`, `-3`...), and every PC follows within 30 s. If the copy fails,
   nothing changes and it can be run again. Browser host:
   `--update-database --switch-plant-data`.
4. The login of the PCs can then be read-only on `stats` (end of
   `stiavelli_compat.sql`); the old tables there can be dropped.

A new site (no 2.0 PC ever) uses `spiga_suivi` from the start.

Production order numbers (`OF-yyyy-nnnn`) are unique in the database: a
number taken meanwhile by another PC is replaced by the next free one, a
number typed by hand that is already used is refused.

## Access control

Roles are data, not code: administrators create as many as they need in
Utilisateurs et rôles, without a developer.

* **Permissions** are declared per module in `PermissionCatalog` (Consulter,
  Exporter, and each module's own: corriger une saisie validée, saisir au-delà
  de 7 jours, débloquer un compte, gérer les rôles...). A new feature adds its
  permissions there; administrators then give them to roles.
* **Roles** are named sets of permissions. They can be created from scratch
  or duplicated, renamed, deactivated (their holders lose them at once) or
  deleted when nobody holds them. Only Administrateur is built in (all
  permissions, not editable). The role templates (direction, responsables,
  chef d'équipe, opérateur) are optional, offered at installation and in
  the roles list, and are ordinary roles once created.
* **Platforms** are named groups of machines (Utilisateurs et rôles ›
  Plateformes, right « Gérer les plateformes »): « Ligne pâte courte 1 »,
  « Conditionnement couscous »... A platform can hold machines from several
  sectors; deactivating it closes its machines to everyone given it, and it
  cannot be deleted while a role assignment names it.
* **Users** hold one or more roles, each with its own **scope** — a
  **team** and one or more **platforms** (none = all the plant) — and
  optional **validity dates** (temporary staff, replacements). A right
  applies where one of the user's roles gives it: the holder sees, enters,
  validates... (whatever the role allows) on the machines of its platforms,
  for its team's postes. Sectors and machines picked one by one, from
  before platforms, still apply and can be removed in the account.
* **One team per account, except responsables.** Only a role with « Vue
  d'ensemble › Couvrir plusieurs équipes » (the direction and responsables
  templates) covers several or all teams; any other account is bound to one
  team, the same on each of its roles. Such an account sees its team's
  postes only: in Conditionnement, the waste entry screens and the
  dashboard (with its Historique and Arrêts), the postes worked by another
  team are greyed out and a click says they do not concern it; the team
  filter is fixed.
* **Delegated administration:** a role with « Créer et modifier des
  comptes » can give only the roles listed as assignable on it, only inside
  its holder's own scope, and only to accounts holding nothing else. It sees
  only its own account and those with a role inside its scope. « Gérer
  les rôles » defines roles and gives any role; only an administrator gives
  Administrateur. No change can leave the application without an active
  administrator.
* **Every screen follows the scope**, the Windows pages included
  (Tableau de bord, Historique, Arrêts, Qualification / Statistiques /
  Planification des arrêts): each page gets the scope of the right it needs,
  and its production and planning repositories (`MachineAccess`) never list,
  read or write a machine outside it — a write on another machine, or moving
  another machine's row, is refused. When the rights change (another
  account, a role or a platform edited) the page reloads with the new ones;
  on sign-out the dashboard empties before the next account.
* **Buttons need a right covering the page:** Excel / PDF exports
  (« Exporter » of the module), « Valider les interventions » on the
  planning board, each held over at least what the page shows.
* **Station-wide outputs need the whole plant:** Paramètres du poste and the
  hall TV screen act on every machine, so only accounts whose right covers
  every machine get them; the Journal d'audit (accounts, rights, entries of
  every line) needs its right over the whole plant and every team, checked
  by its service (`AuditTrailService`). The kiosk mode (`--kiosk`, TV PCs,
  no sign-in) shows the station's machines as before, but its Paramètres
  open only after signing in with an account whose « Modifier les
  paramètres du poste » covers the whole plant (audited, lockout applies);
  when `spiga_suivi` cannot be reached, only for the application started as
  a Windows administrator, to repair the connection.
* **Rights are read again every minute** in the signed-in application,
  not only when the user moves to another screen: a role, platform or
  account changed by an administrator applies within a minute, a disabled
  account is signed out.
* **The dashboard's note and machine renames** record who made them and go
  to the audit trail. The note shows on every screen of the site: changing
  it needs « Modifier la note de l'écran » over the whole plant (given in
  version 2.1 to the roles that had the detailed dashboard); on a TV PC,
  saving it asks for such an account.
* **Planning changes are audited:** stop qualifications (and their
  deletion), maintenance plans, interventions (planned, done, cancelled),
  planned stops and production orders, each with its author, PC, and the
  row before and after, in the same transaction as the change.
* Every service checks the rights itself; the screens only hide what the
  user cannot use.
* Passwords: PBKDF2-SHA256; temporary passwords must be changed at first
  sign-in; lockout after repeated failures; screen lock after inactivity
  (Utilisateurs et rôles › Règles de sécurité).
* Waste entries (interfaces A1 / A2): draft, then validated by another
  person, then locked; a correction needs its own permission and a reason.
  Everything goes to the audit trail (who, when, which PC, old and new value).
