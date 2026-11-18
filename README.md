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
On site, IT runs `database/spiga_suivi.sql` once on the SQL Server.

The desktop shell needs the Microsoft Edge WebView2 runtime (present on
Windows 10 and 11).

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
  its holder's own scope, and only to accounts holding nothing else. « Gérer
  les rôles » defines roles and gives any role; only an administrator gives
  Administrateur. No change can leave the application without an active
  administrator.
* Every service checks the rights itself; the screens only hide what the
  user cannot use.
* Passwords: PBKDF2-SHA256; temporary passwords must be changed at first
  sign-in; lockout after repeated failures; screen lock after inactivity
  (Utilisateurs et rôles › Règles de sécurité).
* Waste entries (interfaces A1 / A2): draft, then validated by another
  person, then locked; a correction needs its own permission and a reason.
  Everything goes to the audit trail (who, when, which PC, old and new value).
