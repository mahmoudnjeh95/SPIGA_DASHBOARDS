namespace Spiga.Core.Waste;

// A1 = déchet d'emballage (per machine), A2 = déchet produit (per sector).
public enum WasteKind
{
    Emballage,
    Produit,
}

// Brouillon: editable by its author. Validee: checked by a second person and
// locked; only a validator can correct it, with a reason, in the audit trail.
public enum WasteStatus
{
    Brouillon,
    Validee,
}

public sealed class WasteEntry
{
    public long Id { get; set; }
    public WasteKind Kind { get; set; }
    public DateTime ProductionDate { get; set; }
    public int ShiftId { get; set; }
    public string Team { get; set; } = "";
    public string Sector { get; set; } = "";
    public string? MachineId { get; set; }
    public double Quantity { get; set; }
    public string Unit { get; set; } = WasteUnits.Kg;
    public string Reason { get; set; } = "";
    public string Comment { get; set; } = "";
    public WasteStatus Status { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTime? ValidatedAt { get; set; }

    public double QuantityKg => WasteUnits.ToKg(Quantity, Unit);

    public string Describe()
        => $"{ProductionDate:dd/MM/yyyy} {Plant.Shifts.Label(ShiftId)} · {MachineId ?? Plant.Sectors.Label(Sector)} · " +
           $"{Quantity.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} {Unit} · {Reason}" +
           (string.IsNullOrWhiteSpace(Comment) ? "" : $" · {Comment}");
}

public static class WasteUnits
{
    public const string Kg = "kg";
    public const string Tonne = "t";
    public static readonly IReadOnlyList<string> All = new[] { Kg, Tonne };

    public static double ToKg(double quantity, string unit) => unit == Tonne ? quantity * 1000 : quantity;
}

public sealed class WasteReason
{
    public int Id { get; set; }
    public WasteKind Kind { get; set; }
    public string Label { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

// What the entry form sends. Team and sector are worked out by the service.
public sealed record WasteDraft(
    WasteKind Kind, DateTime ProductionDate, int ShiftId, string? MachineId, string? Sector,
    double Quantity, string Unit, string Reason, string Comment);

public interface IWasteStore
{
    Task<IReadOnlyList<WasteEntry>> ListAsync(WasteKind? kind, DateTime fromDate, DateTime toDate);
    Task<WasteEntry?> GetAsync(long id);
    Task<long> InsertAsync(WasteEntry entry);
    Task UpdateAsync(WasteEntry entry);
    Task DeleteAsync(long id, string deletedBy, DateTime at);
    Task<IReadOnlyList<WasteReason>> ListReasonsAsync(WasteKind kind);
}
