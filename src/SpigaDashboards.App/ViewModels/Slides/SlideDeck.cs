using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.ViewModels.Slides;

// The deck's default shape, and the order / on-off / duration set in
// Parametres > Affichage applied on top of it. Shared by the dashboard
// (MainViewModel) and the Parametres slide list, so both name the same slides.
public static class SlideDeck
{
    // Sector display order. CC = couscous, PC = pate courte, PL = pate longue.
    // One slide per machine, in machine-id order within its sector.
    public static readonly string[] SectorOrder = { "Couscous", "Pate courte", "Pate longue" };

    // Conditionnement 1 slide; Conditionnement 2 gets every other machine.
    private static readonly string[] Packaging1MachineIds = { "CC03", "CC04" };

    // Default order. pdfPath empty = no document slide, unless listPdf
    // (Parametres: its place can be chosen before a document is).
    public static List<SlideDescriptor> Default(IReadOnlyList<Machine> machines, string pdfPath, bool listPdf = false)
    {
        var slides = new List<SlideDescriptor>();
        foreach (var sector in SectorOrder)
        {
            foreach (var machine in machines.Where(m => m.Sector == sector).OrderBy(m => m.MachineId))
                slides.Add(new MachineSlideDescriptor(sector, machine.MachineId));
        }

        // Slide 8 per the spec's table: admin-selected external document.
        if (listPdf || !string.IsNullOrWhiteSpace(pdfPath))
            slides.Add(new PdfSlideDescriptor(pdfPath));

        // Synthesis, one per sector with machines, same order as above.
        foreach (var sector in SectorOrder)
        {
            var ids = machines.Where(m => m.Sector == sector).OrderBy(m => m.MachineId).Select(m => m.MachineId).ToList();
            if (ids.Count > 0)
                slides.Add(new SynthesisSlideDescriptor(sector, ids));
        }

        // Conditionnement 1 / 2, each followed by its TRS slide, skipped when
        // it has no machine.
        var packagingIds = machines
            .OrderBy(m => Array.IndexOf(SectorOrder, m.Sector))
            .ThenBy(m => m.MachineId)
            .Select(m => m.MachineId)
            .ToList();
        var packaging1 = packagingIds.Where(id => Packaging1MachineIds.Contains(id)).ToList();
        var packaging2 = packagingIds.Except(packaging1).ToList();
        foreach (var (title, ids) in new[] { ("Conditionnement 1", packaging1), ("Conditionnement 2", packaging2) })
        {
            if (ids.Count == 0)
                continue;
            slides.Add(new PackagingSlideDescriptor(title, ids));
            slides.Add(new PackagingSlideDescriptor(title, ids, Trs: true));
        }

        // Cumul of both groups: the run-state photos, then its production and
        // TRS slides. Only when both have machines (otherwise it would repeat
        // the one shown).
        if (packaging1.Count > 0 && packaging2.Count > 0)
        {
            var all = packaging1.Concat(packaging2).ToList();
            slides.Add(new RunStateSlideDescriptor("Cumul", all));
            slides.Add(new PackagingSlideDescriptor("Cumul", all, Cumul: true));
            slides.Add(new PackagingSlideDescriptor("Cumul", all, Trs: true, Cumul: true));
        }

        return slides;
    }

    // Saved order first; a slide the saved list doesn't know (machine added
    // since) goes right after the slide it follows by default.
    public static List<SlideDescriptor> Arrange(List<SlideDescriptor> defaults, IReadOnlyList<SlideSetting> saved)
    {
        var rank = new Dictionary<string, int>();
        foreach (var setting in saved)
            rank.TryAdd(setting.Key, rank.Count);

        var result = defaults.Where(d => rank.ContainsKey(d.Key)).OrderBy(d => rank[d.Key]).ToList();
        var insertAt = 0;
        foreach (var slide in defaults)
        {
            if (rank.ContainsKey(slide.Key))
                insertAt = result.FindIndex(d => d.Key == slide.Key) + 1;
            else
                result.Insert(insertAt++, slide);
        }
        return result;
    }

    // Name of a slide in the Parametres list.
    public static string Label(SlideDescriptor slide, Func<string, string> machineName) => slide switch
    {
        MachineSlideDescriptor m => $"Machine - {machineName(m.MachineId)} ({m.Sector})",
        PdfSlideDescriptor => "Document externe (PDF)",
        SynthesisSlideDescriptor s => $"Synthese - {s.Sector}",
        PackagingSlideDescriptor { Trs: true } p => $"{p.Title} - TRS",
        PackagingSlideDescriptor p => $"{p.Title} - Production",
        RunStateSlideDescriptor r => $"{r.Title} - Etat des machines",
        _ => slide.Key,
    };
}
