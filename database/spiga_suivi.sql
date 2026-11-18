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
-- The tables are created and upgraded by the application's versioned
-- migrations (src/Spiga.Data/Migrations). Least privilege: the login used by
-- the PCs only reads and writes this database (db_datareader,
-- db_datawriter). When a new version changes the tables, IT applies it once,
-- before or right after installing it on the PCs, with a login allowed to
-- change the tables (db_owner or db_ddladmin, or sysadmin):
--     start menu « Mettre à jour la base SPIGA (informatique) »
--     (= SpigaDashboards.App.exe --update-database)
-- Until then the PCs say the base must be updated and do not start on the
-- older tables. Replace spiga_dash below by the login set in the PCs'
-- Paramètres if it is another one. The database name can be changed with
-- "AppDatabaseName" in the PCs' settings.json.
--
-- Sites installed before version 2.1 gave db_ddladmin to the PCs' login:
-- this script takes it back (the PCs could otherwise change or drop tables).

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
    IF IS_ROLEMEMBER('db_ddladmin', 'spiga_dash') = 1
        ALTER ROLE db_ddladmin DROP MEMBER spiga_dash;
END
GO
