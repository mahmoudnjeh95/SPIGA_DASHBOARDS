using System.Globalization;
using System.Text.RegularExpressions;

namespace Spiga.Core.Waste;

// What the waste entry form holds, as typed.
public sealed record WasteFormInput(
    WasteKind Kind, string? Place, string? Quantity, string Unit, string? Reason, string? Comment,
    bool NeedsCorrectionReason = false, string? CorrectionReason = null);

// Checks the entry form while the user types: a value that cannot be right
// is shown at once, what is missing is listed, and the form cannot be sent
// until both are empty. A quantity far above the usual (a typo: 125 for
// 12,5, or t for kg) must be confirmed once. The service checks everything
// again on save; this only spares the user a refused entry.
public sealed partial class WasteFormCheck
{
    public const double MaxKg = 100_000;
    public const int MaxDecimals = 3;

    // Above this a quantity is asked to be confirmed.
    public static double UnusualKg(WasteKind kind) => kind == WasteKind.Emballage ? 200 : 5_000;

    public double? Quantity { get; private init; }
    public double? QuantityKg { get; private init; }
    public string? QuantityError { get; private init; }
    public IReadOnlyList<string> Missing { get; private init; } = Array.Empty<string>();
    public bool Unusual { get; private init; }

    public bool Complete => QuantityError == null && Missing.Count == 0;

    public static WasteFormCheck Of(WasteFormInput input)
    {
        var (quantity, error) = ParseQuantity(input.Quantity);
        double? kg = quantity is { } q ? WasteUnits.ToKg(q, input.Unit) : null;
        if (error == null && kg > MaxKg)
            error = "100 t au plus : vérifiez l'unité (kg ou t).";

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Place))
            missing.Add(input.Kind == WasteKind.Emballage ? "la machine" : "le secteur");
        if (string.IsNullOrWhiteSpace(input.Quantity))
            missing.Add("la quantité");
        if (string.IsNullOrWhiteSpace(input.Reason))
            missing.Add("le motif");
        else if (WasteReasons.NeedsComment(input.Reason) && string.IsNullOrWhiteSpace(input.Comment))
            missing.Add("un commentaire (motif « Autre »)");
        if (input.NeedsCorrectionReason && string.IsNullOrWhiteSpace(input.CorrectionReason))
            missing.Add("le motif de la correction");

        return new WasteFormCheck
        {
            Quantity = error == null ? quantity : null,
            QuantityKg = error == null ? kg : null,
            QuantityError = error,
            Missing = missing,
            Unusual = error == null && kg > UnusualKg(input.Kind),
        };
    }

    [GeneratedRegex(@"[^\d.,]")]
    private static partial Regex NotNumberChar();

    // "12,5", "12.5", "1 250", "0,125" are numbers; "12,5,3", "1e3", "12kg", "-4" are not.
    public static (double? Value, string? Error) ParseQuantity(string? text)
    {
        var t = (text ?? "").Replace(" ", "").Replace(" ", "").Replace(" ", "");
        if (t.Length == 0)
            return (null, null);
        if (NotNumberChar().IsMatch(t))
            return (null, "Chiffres uniquement, avec une virgule pour les décimales (ex. 12,5).");
        var separators = t.Count(c => c is ',' or '.');
        if (separators > 1)
            return (null, "Une seule virgule (ex. 12,5).");
        var parts = t.Split(',', '.');
        if (parts.Length == 2 && parts[1].Length > MaxDecimals)
            return (null, $"{MaxDecimals} décimales au plus (ex. 0,125).");
        if (parts[0].Length > 6)
            return (null, "Quantité trop grande : vérifiez le nombre de chiffres.");
        if (!double.TryParse(t.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return (null, "Nombre invalide (ex. 12,5).");
        if (value <= 0)
            return (null, "La quantité doit être supérieure à 0.");
        return (value, null);
    }
}
