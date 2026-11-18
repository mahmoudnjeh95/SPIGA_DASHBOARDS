-- Version 5: the right « Modifier la note de l'écran » (conditionnement:note).
-- The note was editable by anyone seeing the dashboard: the roles with the
-- detailed dashboard keep it. It applies only to accounts whose right covers
-- the whole plant (the note shows on every screen of the site).
-- Keep in step with Migrations/Sqlite/0005_note_right.sql.
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'conditionnement:note' FROM dbo.RoleGrants g
WHERE g.Permission = 'conditionnement:tableau'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = 'conditionnement:note');
