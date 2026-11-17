-- SPIGA Suivi Production: application database (accounts, roles, waste
-- entries, audit trail, settings). Run once on the site's SQL Server by IT.
--
-- The production database "stats" (Stiavelli) is never written to: the
-- application only reads it (cahier des charges §16).
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
