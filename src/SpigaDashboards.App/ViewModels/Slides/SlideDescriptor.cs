namespace SpigaDashboards.App.ViewModels.Slides;

// Describes *what* a slide should show, computed cheaply up front by
// MainViewModel.BuildSlidesAsync so the deck's shape (which sectors, in what
// order, how many slides) is known before any per-slide content is loaded.
// ShowSlideAsync materializes the matching content view-model on demand.
public abstract record SlideDescriptor;

public sealed record MachineSlideDescriptor(string Sector, string MachineId) : SlideDescriptor;

public sealed record PdfSlideDescriptor(string PdfPath) : SlideDescriptor;

// Machines = the sector's machines kept by the filters, in display order.
public sealed record SynthesisSlideDescriptor(string Sector, IReadOnlyList<string> MachineIds) : SlideDescriptor;

// Conditionnement 1 / 2 / Cumul: production of a group of machines per
// machine and team; Trs = the TRS slide that follows it, Cumul = every
// machine of both groups.
public sealed record PackagingSlideDescriptor(string Title, IReadOnlyList<string> MachineIds, bool Trs = false, bool Cumul = false) : SlideDescriptor;

// Run-state photos of every Cumul machine, the slide before Cumul.
public sealed record RunStateSlideDescriptor(string Title, IReadOnlyList<string> MachineIds) : SlideDescriptor;
