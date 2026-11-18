-- SPIGA Suivi Production: application database (accounts, roles,
-- platforms, waste entries, audit trail, settings, and from version 2.1 the
-- planning of the stops, the dashboard's note and machine names, and the
-- settings shared by the PCs). Run once on the site's SQL Server by IT.
--
-- The production database "stats" (Stiavelli) is never written to: the
-- application only reads it (cahier des charges §16). What versions up to
-- 2.0 wrote in stats is copied here once, by the first PC started on 2.1;
-- update every PC together (a PC left on 2.0 keeps writing in stats, unseen
-- by the others). Every PC, kiosk / TV ones included, needs this database.
--
-- The tables themselves are created by the application at its first start
-- (src/Spiga.Data/Migrations), and upgraded the same way by later versions,
-- so the login used by the PCs needs to be able to create tables in THIS
-- database only (db_ddladmin). Replace spiga_dash below by the login set in
-- the PCs' Paramètres if it is another one. The database name can be changed
-- with "AppDatabaseName" in the PCs' settings.json.

IF DB_ID('spiga_suivi') IS NULL
    CREATE DATABASE spiga_suivi;
GO

USE spiga_suivi;
GO

IF SUSER_ID('spiga_dash') IS NOT NULL AND USER_ID('spiga_dash') IS NULL
    CREATE USER spiga_dash FOR LOGIN spiga_dash;
GO

IF USER_ID('spiga_dash') IS NOT NULL
BEGIN
    ALTER ROLE db_datareader ADD MEMBER spiga_dash;
    ALTER ROLE db_datawriter ADD MEMBER spiga_dash;
    ALTER ROLE db_ddladmin ADD MEMBER spiga_dash;
END
GO
