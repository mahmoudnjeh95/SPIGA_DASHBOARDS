using Dapper;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Data;

public class ProductionRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public ProductionRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<List<Machine>> GetMachinesAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        var machines = await connection.QueryAsync<Machine>(
            "SELECT MachineId, MachineName, MachineType, Sector, IPAddress, IsActive FROM Machines ORDER BY MachineId");
        return machines.ToList();
    }

    public async Task<MachineStatusSnapshot?> GetLatestStatusAsync(string machineId)
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<MachineStatusSnapshot>(
            @"SELECT MachineId, Timestamp, IsConnected, BeltRunning, AlarmActive
              FROM MachineStatusLog
              WHERE MachineId = @machineId
              ORDER BY Timestamp DESC
              LIMIT 1",
            new { machineId });
    }

    public async Task<List<MachineStatusSnapshot>> GetStatusHistoryAsync(string machineId, DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var history = await connection.QueryAsync<MachineStatusSnapshot>(
            @"SELECT MachineId, Timestamp, IsConnected, BeltRunning, AlarmActive
              FROM MachineStatusLog
              WHERE MachineId = @machineId AND Timestamp >= @from AND Timestamp < @to
              ORDER BY Timestamp",
            new { machineId, from = from.ToString("yyyy-MM-dd HH:mm:ss"), to = to.ToString("yyyy-MM-dd HH:mm:ss") });
        return history.ToList();
    }

    public async Task<List<ProductionLot>> GetLotsAsync(string machineId, DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var lots = await connection.QueryAsync<ProductionLot>(
            @"SELECT LotId, MachineId, ProductId, LotNumber, ShiftId, EmployeeId, StartTime, EndTime,
                     TargetNetWeight, MinWeight, MaxWeight, TotalBags, BagsUnderTolerance,
                     BagsInTolerance, BagsOverTolerance, BagsIndeterminate, StdDev, AvgWeight,
                     CorrectedAvgWeight, MetalRejects, IsConform,
                     (SELECT ProductName FROM Products WHERE Products.ProductId = ProductionLots.ProductId) AS ProductName,
                     (SELECT NominalRatePerMinute FROM Products WHERE Products.ProductId = ProductionLots.ProductId) AS NominalRatePerMinute
              FROM ProductionLots
              WHERE MachineId = @machineId AND StartTime >= @from AND StartTime < @to
              ORDER BY StartTime",
            new { machineId, from = from.ToString("yyyy-MM-dd HH:mm:ss"), to = to.ToString("yyyy-MM-dd HH:mm:ss") });
        return lots.ToList();
    }

    // Sector-wide (all machines in that sector, not just one) variants used
    // by the annual synthesis slides.
    public async Task<List<ProductionLot>> GetLotsForSectorAsync(string sector, DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var lots = await connection.QueryAsync<ProductionLot>(
            @"SELECT pl.LotId, pl.MachineId, pl.ProductId, pl.LotNumber, pl.ShiftId, pl.EmployeeId, pl.StartTime, pl.EndTime,
                     pl.TargetNetWeight, pl.MinWeight, pl.MaxWeight, pl.TotalBags, pl.BagsUnderTolerance,
                     pl.BagsInTolerance, pl.BagsOverTolerance, pl.BagsIndeterminate, pl.StdDev, pl.AvgWeight,
                     pl.CorrectedAvgWeight, pl.MetalRejects, pl.IsConform,
                     (SELECT ProductName FROM Products WHERE Products.ProductId = pl.ProductId) AS ProductName,
                     (SELECT NominalRatePerMinute FROM Products WHERE Products.ProductId = pl.ProductId) AS NominalRatePerMinute
              FROM ProductionLots pl
              INNER JOIN Machines m ON m.MachineId = pl.MachineId
              WHERE m.Sector = @sector AND pl.StartTime >= @from AND pl.StartTime < @to
              ORDER BY pl.StartTime",
            new { sector, from = from.ToString("yyyy-MM-dd HH:mm:ss"), to = to.ToString("yyyy-MM-dd HH:mm:ss") });
        return lots.ToList();
    }

    public async Task<List<MachineStatusSnapshot>> GetStatusHistoryForSectorAsync(string sector, DateTime from, DateTime to)
    {
        using var connection = _connectionFactory.CreateConnection();
        var history = await connection.QueryAsync<MachineStatusSnapshot>(
            @"SELECT s.MachineId, s.Timestamp, s.IsConnected, s.BeltRunning, s.AlarmActive
              FROM MachineStatusLog s
              INNER JOIN Machines m ON m.MachineId = s.MachineId
              WHERE m.Sector = @sector AND s.Timestamp >= @from AND s.Timestamp < @to
              ORDER BY s.Timestamp",
            new { sector, from = from.ToString("yyyy-MM-dd HH:mm:ss"), to = to.ToString("yyyy-MM-dd HH:mm:ss") });
        return history.ToList();
    }

    // History browser (spec S6): filters by date range / poste / machine at the
    // DB level. Team isn't stored anywhere, so filtering by team happens in the
    // caller (HistoryViewModel) via TeamScheduleService against each lot's
    // (StartTime, ShiftId) -- same lookup the live "current team" badge uses.
    public async Task<List<ProductionLot>> GetLotsFilteredAsync(
        DateTime from, DateTime to, int? shiftId, string? machineId)
    {
        using var connection = _connectionFactory.CreateConnection();
        var sql = @"SELECT LotId, MachineId, ProductId, LotNumber, ShiftId, EmployeeId, StartTime, EndTime,
                           TargetNetWeight, MinWeight, MaxWeight, TotalBags, BagsUnderTolerance,
                           BagsInTolerance, BagsOverTolerance, BagsIndeterminate, StdDev, AvgWeight,
                           CorrectedAvgWeight, MetalRejects, IsConform,
                           (SELECT ProductName FROM Products WHERE Products.ProductId = ProductionLots.ProductId) AS ProductName,
                           (SELECT NominalRatePerMinute FROM Products WHERE Products.ProductId = ProductionLots.ProductId) AS NominalRatePerMinute
                    FROM ProductionLots
                    WHERE StartTime >= @from AND StartTime < @to";
        if (shiftId.HasValue)
            sql += " AND ShiftId = @shiftId";
        if (!string.IsNullOrEmpty(machineId))
            sql += " AND MachineId = @machineId";
        sql += " ORDER BY StartTime DESC";

        var lots = await connection.QueryAsync<ProductionLot>(
            sql, new { from = from.ToString("yyyy-MM-dd HH:mm:ss"), to = to.ToString("yyyy-MM-dd HH:mm:ss"), shiftId, machineId });
        return lots.ToList();
    }

    public async Task<NoteEntry?> GetActiveNoteAsync()
    {
        using var connection = _connectionFactory.CreateConnection();
        return await connection.QueryFirstOrDefaultAsync<NoteEntry>(
            "SELECT NoteId, Content, CreatedByEmployeeId, CreatedAt, IsActive FROM Notes WHERE IsActive = 1 ORDER BY CreatedAt DESC LIMIT 1");
    }

    public async Task SetActiveNoteAsync(string content)
    {
        using var connection = _connectionFactory.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var employeeId = await connection.ExecuteScalarAsync<int?>(
            "SELECT MIN(EmployeeId) FROM Employees", transaction: transaction) ?? 1;

        await connection.ExecuteAsync("UPDATE Notes SET IsActive = 0 WHERE IsActive = 1", transaction: transaction);
        await connection.ExecuteAsync(
            @"INSERT INTO Notes (Content, CreatedByEmployeeId, CreatedAt, IsActive)
              VALUES (@content, @employeeId, @createdAt, 1)",
            new { content, employeeId, createdAt = DateTime.Now }, transaction: transaction);

        transaction.Commit();
    }

    public async Task UpdateMachineNameAsync(string machineId, string machineName)
    {
        using var connection = _connectionFactory.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE Machines SET MachineName = @machineName WHERE MachineId = @machineId",
            new { machineId, machineName });
    }
}
