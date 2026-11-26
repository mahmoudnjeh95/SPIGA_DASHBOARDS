using Spiga.UI.Services;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels;
using SpigaDashboards.App.ViewModels.Slides;
using Xunit;

namespace Spiga.Tests;

// The web dashboard organised by production line (Paramètres > Lignes):
// each line's overview then its machines, a synthesis per line, the plant
// view; machines not connected yet shown with their line, counted nowhere.
public class DashboardLinesTests
{
    // The plant's machines as named in Paramètres (2026-11-26).
    private static readonly List<Machine> Plant = new[]
    {
        ("CC01", "STV C CC2", "Couscous"), ("CC02", "STV D CC2", "Couscous"), ("CC03", "RICC A CC1", "Couscous"), ("CC04", "RICC B CC1", "Couscous"),
        ("PC01", "STV C PC", "Pate courte"), ("PC02", "STV D PC", "Pate courte"), ("PC03", "STV E PC", "Pate courte"),
        ("PL01", "STV 5 PL", "Pate longue"), ("PL02", "STV 4 PL", "Pate longue"), ("PL03", "STV 3 PL", "Pate longue"),
        ("RIC1", "RIC A PC", "Pate"), ("RIC2", "RIC 2 PL", "Pate"),
    }.Select(m => new Machine { MachineId = m.Item1, MachineName = m.Item2, Sector = m.Item3, IsActive = true }).ToList();

    private static readonly List<DashboardLine> Lines = DashboardLine.Defaults();

    private static List<Machine> Only(params string[] ids) => Plant.Where(m => ids.Contains(m.MachineId)).ToList();

    [Fact]
    public void The_deck_goes_line_by_line_then_the_syntheses_and_the_plant()
    {
        var keys = WebDeck.Default(Plant, Plant, Lines).Select(s => s.Key).ToList();

        Assert.Equal(new[]
        {
            "etat",
            "ligne:CC1", "machine:CC03", "machine:CC04",
            "ligne:CC2", "machine:CC01", "machine:CC02",
            "ligne:PC1", "machine:RIC1",
            "ligne:PC2", "machine:PC01", "machine:PC02", "machine:PC03",
            "ligne:PL1+2", "machine:RIC2", "machine:PL03", "machine:PL02", "machine:PL01",
            "synthese-ligne:CC1", "synthese-ligne:CC2", "synthese-ligne:PC1", "synthese-ligne:PC2", "synthese-ligne:PL1+2",
            "usine",
        }, keys);
    }

    [Fact]
    public void A_machine_not_connected_yet_shows_with_its_whole_line_only()
    {
        var deck = WebDeck.Default(Plant, Plant, Lines);
        var pc1 = Assert.Single(deck.OfType<LineSlideDescriptor>(), s => s.Line == "PC1");
        Assert.Equal(new[] { "RIC1" }, pc1.MachineIds);
        Assert.Equal(new[] { "RIC B PC" }, pc1.Planned);
        Assert.Equal(new[] { "RIC B PC" }, Assert.Single(deck.OfType<LineSynthesisSlideDescriptor>(), s => s.Line == "PC1").Planned);
        // No slide of its own: it has no data.
        Assert.DoesNotContain(deck, s => s.Key.Contains("RIC B", StringComparison.OrdinalIgnoreCase));

        // Another line chosen: PC1 is not shown at all.
        Assert.DoesNotContain(WebDeck.Default(Plant, Only("CC03", "CC04"), Lines), s => s.Key == "ligne:PC1");

        // A line with only part of its machines kept leaves its planned ones out.
        var lines = new List<DashboardLine> { new() { Name = "X", Sector = "Pate courte", MachineIds = { "PC01", "PC02" }, PlannedMachines = { "NEW" } } };
        var part = Assert.Single(WebDeck.Default(Plant, Only("PC01"), lines).OfType<LineSlideDescriptor>());
        Assert.Empty(part.Planned);
        Assert.Equal(new[] { "NEW" }, Assert.Single(WebDeck.Default(Plant, Only("PC01", "PC02"), lines).OfType<LineSlideDescriptor>()).Planned);
    }

    [Fact]
    public void Machines_in_no_line_come_after_the_lines_and_one_line_has_no_plant_view()
    {
        var lines = new List<DashboardLine> { new() { Name = "CC1", Sector = "Couscous", MachineIds = { "CC03", "CC04" } } };
        var keys = WebDeck.Default(Plant, Only("CC03", "CC04", "PL01", "CC01"), lines).Select(s => s.Key).ToList();

        Assert.Equal(new[] { "etat", "ligne:CC1", "machine:CC03", "machine:CC04", "machine:CC01", "machine:PL01", "synthese-ligne:CC1" }, keys);
    }

    [Fact]
    public void The_machine_states_stay_the_first_slide_whatever_the_order_saved()
    {
        var deck = WebDeck.Default(Plant, Plant, Lines);
        Assert.Equal(Plant.Select(m => m.MachineId), Assert.IsType<StatusSlideDescriptor>(deck[0]).MachineIds);

        // Saved with it moved down, and with an order from before it existed.
        var moved = new[] { "ligne:PC2", "etat", "ligne:CC1" }.Select(k => new SlideSetting { Key = k }).ToList();
        Assert.Equal(new[] { "etat", "ligne:PC2" }, WebDeck.Arrange(deck, moved).Take(2).Select(s => s.Key));
        var older = new[] { "ligne:PC2", "ligne:CC1" }.Select(k => new SlideSetting { Key = k }).ToList();
        Assert.Equal("etat", WebDeck.Arrange(deck, older)[0].Key);

        // Filtered to one line: only its machines.
        Assert.Equal(new[] { "CC03", "CC04" }, Assert.IsType<StatusSlideDescriptor>(WebDeck.Default(Plant, Only("CC03", "CC04"), Lines)[0]).MachineIds);
    }

    [Fact]
    public void A_machine_counts_in_its_lines_sector_and_slides_say_their_line()
    {
        var ric1 = Plant.Single(m => m.MachineId == "RIC1");
        Assert.Equal("Pate courte", WebDeck.SectorOf(Lines, ric1));
        Assert.Equal("PL1+2", WebDeck.LineOf(Lines, "ric2")?.Name);

        string Name(string id) => Plant.Single(m => m.MachineId == id).MachineName;
        Assert.Equal("Machine - RIC A PC (Ligne PC1)", WebDeck.Label(new MachineSlideDescriptor("Pate courte", "RIC1"), Name, Lines));
        Assert.Equal("Ligne CC2 - Vue d'ensemble", WebDeck.Label(new LineSlideDescriptor("CC2", new[] { "CC01" }, Array.Empty<string>()), Name, Lines));
        Assert.Equal("Machine - STV C CC2 (Ligne CC2)", WebDeck.Label(new MachineSlideDescriptor("Couscous", "CC01"), Name, Lines));
        Assert.Equal("Synthèse - Ligne PL1+2", WebDeck.Label(new LineSynthesisSlideDescriptor("PL1+2", new[] { "RIC2" }, Array.Empty<string>()), Name, Lines));

        // One colour per line, none twice among the five.
        Assert.Equal(5, Lines.Select(l => WebDeck.ColorOf(Lines, l.Name)).Distinct().Count());
    }

    [Fact]
    public void Lines_typed_in_parametres_are_checked_against_the_machines()
    {
        var ids = Plant.Select(m => m.MachineId).ToList();
        LineRow Row(string name, string machines, string planned = "", string sector = "Couscous")
            => new() { Name = name, Sector = sector, Machines = machines, Planned = planned };

        var (lines, problem) = WebDeck.Parse(new[] { Row("CC1", "cc03; CC04"), Row("PC1", "RIC1", "RIC B PC", "Pate courte"), Row("", "") }, ids);
        Assert.Null(problem);
        Assert.Equal(2, lines!.Count);
        Assert.Equal(new[] { "CC03", "CC04" }, lines[0].MachineIds);
        Assert.Equal(new[] { "RIC B PC" }, lines[1].PlannedMachines);

        Assert.Contains("n'existe pas", WebDeck.Parse(new[] { Row("PC1", "RICB") }, ids).Problem);
        // Saved before, machine gone from the base since: kept, other settings still save.
        Assert.Equal(new[] { "OLD1" }, WebDeck.Parse(new[] { Row("PC1", "old1") }, ids, new[] { "OLD1" }).Lines![0].MachineIds);
        Assert.Contains("dans deux lignes", WebDeck.Parse(new[] { Row("A", "CC01"), Row("B", "CC01") }, ids).Problem);
        Assert.Contains("Deux lignes", WebDeck.Parse(new[] { Row("A", "CC01"), Row("a", "CC02") }, ids).Problem);
        Assert.Contains("secteur", WebDeck.Parse(new[] { Row("A", "CC01", sector: "") }, ids).Problem);
        Assert.Contains("pas de nom", WebDeck.Parse(new[] { Row("", "CC01") }, ids).Problem);
    }

    [Fact]
    public void Lines_and_the_web_order_are_shared_and_an_older_save_keeps_them()
    {
        var settings = new AppSettings();
        settings.DashboardLines = new List<DashboardLine> { new() { Name = "Z", Sector = "Couscous", MachineIds = { "CC01" } } };
        settings.WebSlides = new List<SlideSetting> { new() { Key = "usine", Enabled = false } };

        var other = new AppSettings();
        SharedSettings.Deserialize(SharedSettings.Serialize(settings))!.ApplyTo(other);
        Assert.Equal("Z", Assert.Single(other.DashboardLines).Name);
        Assert.False(Assert.Single(other.WebSlides).Enabled);

        // Saved by a version that didn't know them (the kiosk before this one).
        SharedSettings.Deserialize("{\"StopThresholdMinutes\":5}")!.ApplyTo(other);
        Assert.Equal("Z", Assert.Single(other.DashboardLines).Name);
        Assert.Single(other.WebSlides);
    }

    [Fact]
    public void A_line_chosen_reads_as_its_name_on_the_slides()
    {
        var filter = new DashboardFilter { MachineIds = new[] { "CC03", "CC04" }, MachineNames = new[] { "RICC A CC1", "RICC B CC1" }, ScopeLabel = "Ligne CC1", ShiftId = 1 };
        Assert.Equal("Ligne CC1 · Poste Matin", filter.Describe());
        Assert.Equal("RICC A CC1", (filter with { ScopeLabel = null, MachineIds = new[] { "CC03" }, MachineNames = new[] { "RICC A CC1" }, ShiftId = null }).Describe());
    }
}
