-- Version 3: platforms, named groups of machines defined by the
-- administrators. A role is given over a team and one or more platforms
-- (UserRoles.ScopePlatforms, platform ids separated by commas; empty = all
-- the plant). Keep in step with Migrations/Sqlite/0003_platforms.sql.

CREATE TABLE dbo.Platforms (
    Id          INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    Name        NVARCHAR(60)  NOT NULL UNIQUE,
    Description NVARCHAR(400) NOT NULL DEFAULT '',
    IsActive    BIT           NOT NULL DEFAULT 1,
    CreatedAt   DATETIME2(0)  NOT NULL,
    UpdatedAt   DATETIME2(0)  NOT NULL
);
GO

-- The machines of a platform, with their sector (production database keys).
CREATE TABLE dbo.PlatformMachines (
    PlatformId INT           NOT NULL REFERENCES dbo.Platforms(Id) ON DELETE CASCADE,
    MachineId  NVARCHAR(40)  NOT NULL,
    Sector     NVARCHAR(40)  NOT NULL,
    CONSTRAINT PK_PlatformMachines PRIMARY KEY (PlatformId, MachineId)
);
GO

ALTER TABLE dbo.UserRoles ADD ScopePlatforms NVARCHAR(400) NOT NULL DEFAULT '';
GO

-- "Gérer les plateformes" for the roles that already manage the roles.
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'utilisateurs:plateformes' FROM dbo.RoleGrants g
WHERE g.Permission = 'utilisateurs:roles'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = 'utilisateurs:plateformes');
