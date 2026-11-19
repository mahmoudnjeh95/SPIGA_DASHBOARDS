using System.Globalization;
using System.Text;
using Spiga.Core.Plant;

namespace Spiga.UI.Components;

// Picture files shipped in wwwroot/img/ (motifs/, products/): never
// loaded from a URL, the plant PCs may be offline. The lists below are the
// files actually present - a key missing here keeps its inline drawing, so
// add a line when you add a file (name = FileKey of the label + extension).
public static class Artwork
{
    private const string Root = "_content/Spiga.UI/img/";

    // Waste motifs, by FileKey of the motif label (the two "Autre" share autre).
    private static readonly Dictionary<string, string> Motifs = new()
    {
        ["reglage-machine"] = "reglage-machine.png",
        ["changement-de-format"] = "changement-de-format.png",
        ["changement-de-bobine"] = "changement-de-bobine.png",
        ["defaut-de-soudure"] = "defaut-de-soudure.png",
        ["film-defectueux"] = "film-defectueux.png",
        ["impression-marquage"] = "impression-marquage.png",
        ["autre"] = "autre.png",
        ["demarrage-de-ligne"] = "demarrage-de-ligne.png",
        ["arret-de-ligne"] = "arret-de-ligne.png",
        ["hors-specification"] = "hors-specification.png",
        ["produit-casse-brisures"] = "produit-casse-brisures.png",
        ["vidange-nettoyage"] = "vidange-nettoyage.png",
    };

    // Products, by machine sector.
    private static readonly Dictionary<string, string> Products = new()
    {
        [Sectors.Couscous] = "couscous.png",
        [Sectors.PateCourte] = "pate-courte.png",
        [Sectors.PateLongue] = "pate-longue.png",
    };

    // "Impression / marquage" -> "impression-marquage", "À l'arrêt" -> "a-l-arret".
    public static string FileKey(string label)
    {
        var d = (label ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    public static string? Motif(string label)
        => Motifs.TryGetValue(FileKey(label), out var f) ? Root + "motifs/" + f : null;

    public static string? Product(string sector)
        => Products.TryGetValue(sector ?? "", out var f) ? Root + "products/" + f : null;
}
