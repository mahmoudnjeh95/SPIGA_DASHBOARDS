using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels.Slides;

namespace Spiga.UI.Services;

// The web dashboard's deck, organised by production line (Paramètres >
// Lignes): each line's overview followed by its machines, then a synthesis
// per line and the plant view. The kiosk keeps its own deck (SlideDeck), by
// sector, and its own saved order (AppSettings.Slides / WebSlides).

// A line's overview: its figures, the live state of each machine and the
// machine x team table. Machines = those the filters keep; Planned = its
// machines not connected yet, only when the whole line is shown.
public sealed record LineSlideDescriptor(string Line, IReadOnlyList<string> MachineIds, IReadOnlyList<string> Planned) : SlideDescriptor
{
    public override string Key => $"ligne:{Line}";
}

// The synthesis of one line (month, 3 months, year).
public sealed record LineSynthesisSlideDescriptor(string Line, IReadOnlyList<string> MachineIds, IReadOnlyList<string> Planned) : SlideDescriptor
{
    public override string Key => $"synthese-ligne:{Line}";
}

// Every line side by side.
public sealed record PlantSlideDescriptor(IReadOnlyList<string> Lines) : SlideDescriptor
{
    public override string Key => "usine";
}

public static class WebDeck
{
    // One colour per line, in line order: none of them green, orange or red,
    // which mean good / watch / bad everywhere on the slides.
    private static readonly string[] Palette = { "#2563EB", "#0D9488", "#9333EA", "#DB2777", "#475569", "#0891B2", "#4F46E5", "#A21CAF" };

    public static string ColorOf(IReadOnlyList<DashboardLine> lines, string line)
    {
        var index = lines.ToList().FindIndex(l => l.Name == line);
        return ColorAt(index < 0 ? lines.Count : index);
    }

    // The colour of the line at this place in Paramètres > Lignes.
    public static string ColorAt(int index) => Palette[index % Palette.Length];

    public static DashboardLine? LineOf(IReadOnlyList<DashboardLine> lines, string machineId)
        => lines.FirstOrDefault(l => l.MachineIds.Contains(machineId, StringComparer.OrdinalIgnoreCase));

    // A machine's sector on the dashboard: its line's, else its own.
    public static string SectorOf(IReadOnlyList<DashboardLine> lines, Machine machine)
        => LineOf(lines, machine.MachineId)?.Sector is { Length: > 0 } sector ? sector : machine.Sector;

    // The default order. `all` = every machine the account sees, `shown` =
    // those the filters keep: a line's planned machines only come with the
    // whole line. Machines in no line come after the lines, by sector.
    public static List<SlideDescriptor> Default(IReadOnlyList<Machine> all, IReadOnlyList<Machine> shown, IReadOnlyList<DashboardLine> lines)
    {
        var shownIds = shown.Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allIds = all.Select(m => m.MachineId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parts = lines.Select(line =>
        {
            var known = line.MachineIds.Where(allIds.Contains).ToList();
            var ids = known.Where(shownIds.Contains).ToList();
            // A line with nothing shown keeps nothing, planned machines included.
            var whole = ids.Count == known.Count && (ids.Count > 0 || shown.Count == all.Count);
            return (Line: line, Ids: ids, Planned: whole ? line.PlannedMachines : (IReadOnlyList<string>)Array.Empty<string>());
        }).ToList();

        var slides = new List<SlideDescriptor>();
        foreach (var (line, ids, planned) in parts)
        {
            if (ids.Count == 0 && planned.Count == 0)
                continue;
            slides.Add(new LineSlideDescriptor(line.Name, ids, planned));
            slides.AddRange(ids.Select(id => new MachineSlideDescriptor(line.Sector, id)));
        }

        var inLines = lines.SelectMany(l => l.MachineIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var machine in shown.Where(m => !inLines.Contains(m.MachineId))
                     .OrderBy(m => Array.IndexOf(SlideDeck.SectorOrder, m.Sector) is var i && i < 0 ? int.MaxValue : i).ThenBy(m => m.MachineId))
            slides.Add(new MachineSlideDescriptor(machine.Sector, machine.MachineId));

        foreach (var (line, ids, planned) in parts.Where(p => p.Ids.Count > 0))
            slides.Add(new LineSynthesisSlideDescriptor(line.Name, ids, planned));

        var producing = parts.Where(p => p.Ids.Count > 0).Select(p => p.Line.Name).ToList();
        if (producing.Count >= 2)
            slides.Add(new PlantSlideDescriptor(producing));
        return slides;
    }

    // Name of a slide (deck bar, dots, Paramètres).
    public static string Label(SlideDescriptor slide, Func<string, string> machineName, IReadOnlyList<DashboardLine> lines) => slide switch
    {
        LineSlideDescriptor l => $"{DashboardLine.LabelOf(l.Line)} - Vue d'ensemble",
        LineSynthesisSlideDescriptor s => $"Synthèse - {DashboardLine.LabelOf(s.Line)}",
        PlantSlideDescriptor => "Vue usine - toutes les lignes",
        MachineSlideDescriptor m when LineOf(lines, m.MachineId) is { } line => $"Machine - {machineName(m.MachineId)} ({line.Label})",
        MachineSlideDescriptor m => $"Machine - {machineName(m.MachineId)} ({Machine.SectorLabel(m.Sector)})",
        _ => slide.Key,
    };

    // Lines as typed in Paramètres > Lignes: names unique and filled in, a
    // known sector, every machine in one line only, and every machine id
    // newly typed known to the database. An id already saved stays even if
    // its machine is gone from the database (it just isn't shown), so a
    // machine taken out never blocks saving the other settings. Returns the
    // lines, or the first problem.
    public static (List<DashboardLine>? Lines, string? Problem) Parse(IEnumerable<LineRow> rows, IReadOnlyCollection<string> machineIds,
        IReadOnlyCollection<string>? savedIds = null)
    {
        var allowed = machineIds.Concat(savedIds ?? Array.Empty<string>()).ToList();
        var lines = new List<DashboardLine>();
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            var ids = Split(row.Machines);
            var planned = Split(row.Planned);
            if (name.Length == 0)
            {
                if (ids.Count == 0 && planned.Count == 0)
                    continue;
                return (null, "Une ligne n'a pas de nom.");
            }
            if (name.Length > 20)
                return (null, $"Le nom « {name} » est trop long (20 caractères au plus).");
            if (lines.Any(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)))
                return (null, $"Deux lignes s'appellent « {name} ».");
            if (!SlideDeck.SectorOrder.Contains(row.Sector))
                return (null, $"Choisissez le secteur de la ligne {name}.");
            var known = new List<string>();
            foreach (var id in ids)
            {
                var match = allowed.FirstOrDefault(m => string.Equals(m, id, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                    return (null, $"Ligne {name} : la machine « {id} » n'existe pas dans la base. Une machine pas encore raccordée va dans « En préparation ».");
                if (taken.TryGetValue(match, out var other))
                    return (null, $"La machine {match} est dans deux lignes ({other} et {name}).");
                taken[match] = name;
                known.Add(match);
            }
            lines.Add(new DashboardLine { Name = name, Sector = row.Sector, MachineIds = known, PlannedMachines = planned });
        }
        return (lines, null);
    }

    // "CC03, CC04" / "CC03;CC04" / one per line; duplicates dropped.
    public static List<string> Split(string text)
        => text.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

// One line of Paramètres > Lignes, as typed.
public sealed class LineRow
{
    public string Name { get; set; } = "";
    public string Sector { get; set; } = "";
    public string Machines { get; set; } = "";
    public string Planned { get; set; } = "";

    public static LineRow Of(DashboardLine line) => new()
    {
        Name = line.Name,
        Sector = line.Sector,
        Machines = string.Join(", ", line.MachineIds),
        Planned = string.Join(", ", line.PlannedMachines),
    };
}
