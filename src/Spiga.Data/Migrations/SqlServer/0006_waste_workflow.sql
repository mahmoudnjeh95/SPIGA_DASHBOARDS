-- Version 6: waste entry workflow (see the SQLite script for the details).
ALTER TABLE dbo.WasteEntries ADD
    Version       INT           NOT NULL CONSTRAINT DF_WasteEntries_Version DEFAULT 1,
    ReturnedBy    NVARCHAR(40)  NULL,
    ReturnedAt    DATETIME2(0)  NULL,
    ReturnComment NVARCHAR(300) NULL,
    CancelledBy   NVARCHAR(40)  NULL,
    CancelledAt   DATETIME2(0)  NULL,
    CancelReason  NVARCHAR(300) NULL;
GO

CREATE TABLE dbo.WasteRevisions (
    Id        BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    EntryId   BIGINT        NOT NULL,
    Version   INT           NOT NULL,
    At        DATETIME2(0)  NOT NULL,
    UserLogin NVARCHAR(40)  NOT NULL,
    Action    NVARCHAR(60)  NOT NULL,
    Status    VARCHAR(12)   NOT NULL,
    Snapshot  NVARCHAR(800) NOT NULL,
    Note      NVARCHAR(300) NOT NULL DEFAULT ''
);
CREATE INDEX IX_WasteRevisions_Entry ON dbo.WasteRevisions (EntryId, Version);
GO

INSERT INTO dbo.WasteRevisions (EntryId, Version, At, UserLogin, Action, Status, Snapshot, Note)
SELECT Id, 1, CreatedAt, CreatedBy, N'Saisie créée', Status,
       CONCAT(CAST(Quantity AS NVARCHAR(20)), N' ', Unit, N' · ', Reason, CASE WHEN Comment = N'' THEN N'' ELSE CONCAT(N' · ', Comment) END),
       N'Reprise de l''historique'
FROM dbo.WasteEntries;
GO

INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT DISTINCT g.RoleId, p.Permission
FROM dbo.RoleGrants g JOIN dbo.Roles r ON r.Id = g.RoleId
CROSS JOIN (VALUES ('saisie-dechets:produit'), ('saisie-dechets:autres-postes')) p(Permission)
WHERE g.Permission = 'saisie-dechets:saisir' AND r.Code <> 'operateur'
  AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = g.RoleId AND x.Permission = p.Permission);
INSERT INTO dbo.RoleGrants (RoleId, Permission)
SELECT r.Id, 'dechets:voir' FROM dbo.Roles r
WHERE r.Code = 'operateur' AND NOT EXISTS (SELECT 1 FROM dbo.RoleGrants x WHERE x.RoleId = r.Id AND x.Permission = 'dechets:voir');
GO
