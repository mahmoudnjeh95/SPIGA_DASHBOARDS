-- Version 5: the right « Modifier la note de l'écran » (conditionnement:note).
-- The note was editable by anyone seeing the dashboard: the roles with the
-- detailed dashboard keep it. It applies only to accounts whose right covers
-- the whole plant (the note shows on every screen of the site).
-- Keep in step with Migrations/SqlServer/0005_note_right.sql.
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT RoleId, 'conditionnement:note' FROM RoleGrants WHERE Permission = 'conditionnement:tableau';
