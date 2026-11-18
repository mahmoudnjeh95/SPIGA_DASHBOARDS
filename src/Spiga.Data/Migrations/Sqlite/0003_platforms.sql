-- Version 3: platforms, named groups of machines defined by the
-- administrators. A role is given over a team and one or more platforms
-- (UserRoles.ScopePlatforms, platform ids separated by commas; empty = all
-- the plant). Keep in step with Migrations/SqlServer/0003_platforms.sql.

CREATE TABLE Platforms (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    Description TEXT    NOT NULL DEFAULT '',
    IsActive    INTEGER NOT NULL DEFAULT 1,
    CreatedAt   TEXT    NOT NULL,
    UpdatedAt   TEXT    NOT NULL
);

-- The machines of a platform, with their sector (production database keys).
CREATE TABLE PlatformMachines (
    PlatformId INTEGER NOT NULL REFERENCES Platforms(Id) ON DELETE CASCADE,
    MachineId  TEXT    NOT NULL,
    Sector     TEXT    NOT NULL,
    PRIMARY KEY (PlatformId, MachineId)
);

ALTER TABLE UserRoles ADD COLUMN ScopePlatforms TEXT NOT NULL DEFAULT '';

-- "Gérer les plateformes" for the roles that already manage the roles.
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT RoleId, 'utilisateurs:plateformes' FROM RoleGrants WHERE Permission = 'utilisateurs:roles';
