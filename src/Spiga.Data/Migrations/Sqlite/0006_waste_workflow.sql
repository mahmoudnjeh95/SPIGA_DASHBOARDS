-- Version 6: waste entry workflow.
-- * Version: each change raises it; an action sent on an older version is
--   refused (the validator validates what they saw, nothing else).
-- * Renvoyee (sent back to the author with a comment) and Annulee (a
--   validated entry withdrawn with a reason) statuses.
-- * WasteRevisions: every state of every entry, shown on the entry screens.
ALTER TABLE WasteEntries ADD COLUMN Version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE WasteEntries ADD COLUMN ReturnedBy TEXT NULL;
ALTER TABLE WasteEntries ADD COLUMN ReturnedAt TEXT NULL;
ALTER TABLE WasteEntries ADD COLUMN ReturnComment TEXT NULL;
ALTER TABLE WasteEntries ADD COLUMN CancelledBy TEXT NULL;
ALTER TABLE WasteEntries ADD COLUMN CancelledAt TEXT NULL;
ALTER TABLE WasteEntries ADD COLUMN CancelReason TEXT NULL;

CREATE TABLE WasteRevisions (
    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
    EntryId   INTEGER NOT NULL,
    Version   INTEGER NOT NULL,
    At        TEXT    NOT NULL,
    UserLogin TEXT    NOT NULL,
    Action    TEXT    NOT NULL,
    Status    TEXT    NOT NULL,
    Snapshot  TEXT    NOT NULL,
    Note      TEXT    NOT NULL DEFAULT ''
);
CREATE INDEX IX_WasteRevisions_Entry ON WasteRevisions (EntryId, Version);

-- The entries made before this version: their creation as first revision.
INSERT INTO WasteRevisions (EntryId, Version, At, UserLogin, Action, Status, Snapshot, Note)
SELECT Id, 1, CreatedAt, CreatedBy, 'Saisie créée', Status,
       Quantity || ' ' || Unit || ' · ' || Reason || CASE WHEN Comment = '' THEN '' ELSE ' · ' || Comment END, 'Reprise de l''historique'
FROM WasteEntries;

-- Split rights. "saisir" now means packaging waste (per machine); product
-- waste (per sector) and entering for a past poste are their own rights.
-- Existing roles that could enter keep everything, except the operator
-- template, which keeps packaging waste on its current poste and gains the
-- waste dashboard (inside its scope).
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'saisie-dechets:produit' FROM RoleGrants g JOIN Roles r ON r.Id = g.RoleId
WHERE g.Permission = 'saisie-dechets:saisir' AND r.Code <> 'operateur';
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT g.RoleId, 'saisie-dechets:autres-postes' FROM RoleGrants g JOIN Roles r ON r.Id = g.RoleId
WHERE g.Permission = 'saisie-dechets:saisir' AND r.Code <> 'operateur';
INSERT OR IGNORE INTO RoleGrants (RoleId, Permission)
SELECT Id, 'dechets:voir' FROM Roles WHERE Code = 'operateur';
