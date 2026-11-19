namespace Spiga.Core.Waste;

// A1 = déchet d'emballage (per machine), A2 = déchet produit (per sector).
public enum WasteKind
{
    Emballage,
    Produit,
}

// Brouillon: entered, editable by its author, waiting for a validator.
// Renvoyee: sent back to the author with a comment; the author corrects it
// and it becomes a Brouillon again ("soumise à nouveau").
// Validee: checked by a second person and locked; only a holder of
// "Corriger ou annuler" can change it (Corriger) or withdraw it (Annulee),
// always with a reason. Annulee entries count nowhere but stay visible.
public enum WasteStatus
{
    Brouillon,
    Renvoyee,
    Validee,
    Annulee,
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

    // Raised by every change. Actions carry the version the user saw: an
    // action on an entry changed since is refused (see WasteService).
    public int Version { get; set; } = 1;
    public string? ReturnedBy { get; set; }
    public DateTime? ReturnedAt { get; set; }
    public string? ReturnComment { get; set; }
    public string? CancelledBy { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelReason { get; set; }

    // Counted in the totals: everything but the cancelled entries.
    public bool Counts => Status != WasteStatus.Annulee;

    public bool IsOpen => Status is WasteStatus.Brouillon or WasteStatus.Renvoyee;

    // Its content was changed after it was entered.
    public bool WasModified => UpdatedAt != null;

    public double QuantityKg => WasteUnits.ToKg(Quantity, Unit);

    // What the entry says, without where/when (the revision snapshots).
    public string Content()
        => $"{(MachineId ?? Plant.Sectors.Label(Sector))} · {Quantity.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} {Unit} · {Reason}" +
           (string.IsNullOrWhiteSpace(Comment) ? "" : $" · {Comment}");

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

public static class WasteReasons
{
    // A catch-all motif says nothing by itself: the comment must explain it.
    public static bool NeedsComment(string? reason) => string.Equals(reason?.Trim(), "Autre", StringComparison.OrdinalIgnoreCase);
}

// What the entry form sends. Team and sector are worked out by the service.
public sealed record WasteDraft(
    WasteKind Kind, DateTime ProductionDate, int ShiftId, string? MachineId, string? Sector,
    double Quantity, string Unit, string Reason, string Comment);

public static class WasteStatuses
{
    public static string Label(WasteStatus status) => status switch
    {
        WasteStatus.Brouillon => "À valider",
        WasteStatus.Renvoyee => "Renvoyée",
        WasteStatus.Validee => "Validée",
        _ => "Annulée",
    };

    public static string Css(WasteStatus status) => status switch
    {
        WasteStatus.Brouillon => "warn",
        WasteStatus.Renvoyee => "bad",
        WasteStatus.Validee => "ok",
        _ => "",
    };
}

// One state of an entry: written by every action, never changed. The
// history shown on the entry screens.
public sealed class WasteRevision
{
    public long Id { get; set; }
    public long EntryId { get; set; }
    public int Version { get; set; }
    public DateTime At { get; set; }
    public string UserLogin { get; set; } = "";
    public string Action { get; set; } = "";
    public WasteStatus Status { get; set; }
    public string Snapshot { get; set; } = "";
    public string Note { get; set; } = "";
}

public interface IWasteStore
{
    Task<IReadOnlyList<WasteEntry>> ListAsync(WasteKind? kind, DateTime fromDate, DateTime toDate);
    Task<WasteEntry?> GetAsync(long id);
    Task<long> InsertAsync(WasteEntry entry, WasteRevision revision);

    // Saves the entry (its Version already raised) only if the stored
    // version is still expectedVersion, with its revision, in one
    // transaction. False = someone changed it in between.
    Task<bool> UpdateAsync(WasteEntry entry, int expectedVersion, WasteRevision revision);
    Task<bool> DeleteAsync(long id, int expectedVersion, string deletedBy, DateTime at, WasteRevision revision);
    Task<IReadOnlyList<WasteRevision>> ListRevisionsAsync(long entryId);
    Task<IReadOnlyList<WasteReason>> ListReasonsAsync(WasteKind kind);
}
