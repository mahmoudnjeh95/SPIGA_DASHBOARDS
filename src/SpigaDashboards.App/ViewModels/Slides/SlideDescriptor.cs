namespace SpigaDashboards.App.ViewModels.Slides;

// Describes *what* a slide should show, computed cheaply up front by
// MainViewModel.BuildSlidesAsync so the deck's shape (which sectors, in what
// order, how many slides) is known before any per-slide content is loaded.
// ShowSlideAsync materializes the matching content view-model on demand.
public abstract record SlideDescriptor;

public sealed record MachinePairSlideDescriptor(string Sector, IReadOnlyList<string> MachineIds) : SlideDescriptor;

public sealed record PdfSlideDescriptor(string PdfPath) : SlideDescriptor;

public sealed record AnnualSummarySlideDescriptor(string Sector) : SlideDescriptor;
