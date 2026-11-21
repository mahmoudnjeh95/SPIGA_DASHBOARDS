-- Version 8: what an account may open after sign-in. « Ouvrir SPIGA Suivi
-- Production » (application:voir) and « Ouvrir SPIGA Dashboard »
-- (conditionnement:dashboard), beside « Ouvrir SPIGA TV » (conditionnement:tv).
-- Every role so far opened the application: they keep it; the roles with
-- the detailed dashboard get SPIGA Dashboard too. A TV-only or
-- dashboard-only account is then a role without the application right.
-- Keep in step with Migrations/SqlServer/0008_open_rights.sql.
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT Id, 'application:voir' FROM Roles WHERE IsSystem = 0;
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT RoleId, 'conditionnement:dashboard' FROM RoleGrants WHERE Permission = 'conditionnement:tableau';
