using Spiga.UI.Services;

namespace Spiga.Tests;

// The audit journal and the entry history keep the ids they were written
// with; the screens show the names of Paramètres > Machines.
public class MachineNamesTests
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CC01"] = "Controle CC01",
        ["PC01"] = "Peseuse PC01",
        ["PC10"] = "Ligne Penne",
    };

    [Theory]
    [InlineData("PC01", "Peseuse PC01")]
    [InlineData("Machine : PC01 · Tâche : Graissage", "Machine : Peseuse PC01 · Tâche : Graissage")]
    [InlineData("18/11/2026 Matin · CC01 · 2 kg · Film", "18/11/2026 Matin · Controle CC01 · 2 kg · Film")]
    [InlineData("PC10, PC01", "Ligne Penne, Peseuse PC01")]
    [InlineData("Controle CC01", "Controle CC01")]          // already the name: not doubled
    [InlineData("OF-2026-PC011 sur pc01", "OF-2026-PC011 sur Peseuse PC01")] // whole ids only
    [InlineData("Saisie n°12", "Saisie n°12")]
    [InlineData(null, "")]
    public void Ids_in_recorded_texts_show_as_names(string? text, string shown)
        => Assert.Equal(shown, MachineNames.Replace(text, Names));
}
