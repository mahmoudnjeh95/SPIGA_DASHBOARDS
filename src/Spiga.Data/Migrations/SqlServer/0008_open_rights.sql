-- Version 8: what an account may open after sign-in. « Ouvrir SPIGA Suivi
-- Production » (application:voir) and « Ouvrir SPIGA Dashboard »
-- (conditionnement:dashboard), beside « Ouvrir SPIGA TV » (conditionnement:tv).
-- Every role so far opened the application: they keep it; the roles with
-- the detailed dashboard get SPIGA Dashboard too. A TV-only or
-- dashboard-only account is then a role without the application right.
-- Keep in step with Migrations/Sqlite/0008_open_rights.sql.
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT r.Id, 'application:voir' FROM dbo.Roles r
WHERE r.IsSystem = 0
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = r.Id AND x.Permission = 'application:voir');
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'conditionnement:dashboard' FROM dbo.RoleGrants g
WHERE g.Permission = 'conditionnement:tableau'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = 'conditionnement:dashboard');
