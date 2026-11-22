-- Version 10: the day an employee left (see the SQLite script for the details).
ALTER TABLE dbo.StaffEmployees ADD InactiveFrom DATETIME2(0) NULL;
GO

UPDATE e
SET InactiveFrom = COALESCE(
    (SELECT DATEADD(DAY, 1, MAX(s.ProductionDate))
     FROM dbo.AttendanceLines l JOIN dbo.AttendanceSheets s ON s.Id = l.SheetId
     WHERE l.EmployeeId = e.Id),
    CAST(CAST(GETDATE() AS DATE) AS DATETIME2(0)))
FROM dbo.StaffEmployees e
WHERE e.IsActive = 0;
