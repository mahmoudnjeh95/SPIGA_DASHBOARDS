-- Settings shared by every PC of SPIGA Dashboards (cadences, pauses, stop
-- threshold, teams, TRS targets: Models/SharedSettings.cs), in the stats
-- database beside the app's other tables. The app creates the table itself
-- when its login may (SharedSettingsRepository.EnsureSchemaAsync, same
-- statement); run this script once when the PCs connect with a login that
-- may not CREATE TABLE, such as spiga_dash. Idempotent. The Stiavelli tables
-- (produzione, lotto, ...) are not touched.

IF OBJECT_ID('dbo.AppSharedSettings') IS NULL
CREATE TABLE dbo.AppSharedSettings (
    Id INT NOT NULL PRIMARY KEY CHECK (Id = 1),
    Version INT NOT NULL,
    Json NVARCHAR(MAX) NOT NULL,
    UpdatedAt DATETIME2(0) NOT NULL,
    UpdatedBy NVARCHAR(100) NOT NULL);
GO

-- The dashboard PCs' login reads (db_datareader) and saves the row.
IF USER_ID('spiga_dash') IS NOT NULL
    GRANT INSERT, UPDATE ON dbo.AppSharedSettings TO spiga_dash;
GO
