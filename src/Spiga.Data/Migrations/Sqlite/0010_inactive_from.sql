-- Version 10: the day an employee left (StaffEmployees.InactiveFrom). A
-- past poste opened after he was made inactive still lists him; from that
-- day on he is off the sheets. Employees already inactive get the day
-- after the last sheet they are on (today when they are on none).
-- Keep in step with Migrations/SqlServer/0010_inactive_from.sql.
ALTER TABLE StaffEmployees ADD COLUMN InactiveFrom TEXT NULL;

UPDATE StaffEmployees
SET InactiveFrom = COALESCE(
    (SELECT strftime('%Y-%m-%d 00:00:00', MAX(s.ProductionDate), '+1 day')
     FROM AttendanceLines l JOIN AttendanceSheets s ON s.Id = l.SheetId
     WHERE l.EmployeeId = StaffEmployees.Id),
    strftime('%Y-%m-%d 00:00:00', 'now', 'localtime'))
WHERE IsActive = 0;
