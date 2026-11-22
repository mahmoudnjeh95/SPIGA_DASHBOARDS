-- Version 11: finished production days, kept (see the SQLite script for the details).
CREATE TABLE dbo.ProductionDayCache (
    Source      CHAR(16)       NOT NULL,
    MachineId   NVARCHAR(40)   NOT NULL,
    Day         INT            NOT NULL,
    Size        INT            NOT NULL,
    Weights     INT            NOT NULL,
    Fingerprint CHAR(32)       NOT NULL,
    Data        VARBINARY(MAX) NOT NULL,
    SavedAt     DATETIME2(0)   NOT NULL,
    CONSTRAINT PK_ProductionDayCache PRIMARY KEY (Source, Day, Size, Weights, MachineId)
);
