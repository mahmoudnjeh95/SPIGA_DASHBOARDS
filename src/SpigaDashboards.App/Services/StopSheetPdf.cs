using System.Globalization;
using System.IO;
using System.Windows;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// Printable "fiche des arrets": one sheet per poste (date + poste + team),
// listing that poste's stops with blank columns the team fills in by hand
// (cause, action), and a signature line. A4 landscape, SPIGA logo on every page.
public static class StopSheetPdf
{
    private const double PageWidth = 842, PageHeight = 595;
    private const double Margin = 30;
    private const double RowHeight = 28;
    private const double FooterTop = PageHeight - 62;

    private static readonly XFont TitleFont = new("Arial", 16, XFontStyle.Bold);
    private static readonly XFont InfoFont = new("Arial", 11, XFontStyle.Bold);
    private static readonly XFont SmallFont = new("Arial", 8, XFontStyle.Regular);
    private static readonly XFont HeaderFont = new("Arial", 8.5, XFontStyle.Bold);
    private static readonly XFont CellFont = new("Arial", 9, XFontStyle.Regular);
    private static readonly XFont FooterFont = new("Arial", 10, XFontStyle.Regular);
    private static readonly XBrush HeaderFill = new XSolidBrush(XColor.FromArgb(0xDC, 0xE6, 0xF1));
    private static readonly XBrush NoDataFill = new XSolidBrush(XColor.FromArgb(0xF3, 0xF3, 0xF3));
    private static readonly XPen GridPen = new(XColor.FromArgb(0x99, 0x99, 0x99), 0.6);

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private static readonly (string Header, double Width)[] Columns =
    {
        ("Machine", 115), ("Début", 42), ("Fin", 42), ("Durée", 45), ("Pause", 48), ("Type", 58),
        ("Cause de l'arrêt (à remplir par l'équipe)", 215), ("Action / commentaire", 167), ("Visa", 50),
    };

    public static void Export(IReadOnlyList<ProductionStop> stops, AppSettings settings, string path)
    {
        var document = new PdfDocument();
        document.Info.Title = "Fiche des arrêts de production";
        var logo = LoadLogo();
        var settingsLine = $"Seuil : {settings.StopThresholdMinutes} min  -  Pauses planifiées déduites : "
                           + BreakSchedule.Describe(settings.PlannedBreaks);
        var generated = DateTime.Now;

        foreach (var poste in stops.GroupBy(s => (s.ShiftDate, s.ShiftId, s.Team)))
        {
            var list = poste.ToList();
            var posteLine = $"Date : {poste.Key.ShiftDate.ToString("dddd dd/MM/yyyy", French)}     "
                            + $"Poste : {KpiCalculator.ShiftName(poste.Key.ShiftId)} ({KpiCalculator.ShiftTimeRange(poste.Key.ShiftId)})     "
                            + $"Équipe : {poste.Key.Team}";
            var summary = $"{list.Count} arrêt(s), durée totale hors pauses : {ProductionStop.FormatDuration(list.Sum(s => s.NetMinutes))}";

            XGraphics? gfx = null;
            double y = 0;
            var continued = false;

            void NewPage()
            {
                gfx?.Dispose();
                var page = document.AddPage();
                page.Width = XUnit.FromPoint(PageWidth);
                page.Height = XUnit.FromPoint(PageHeight);
                gfx = XGraphics.FromPdfPage(page);
                y = DrawHeader(gfx, logo, posteLine, settingsLine, continued ? summary + "  (suite)" : summary, generated);
                DrawFooter(gfx);
                y = DrawTableHeader(gfx, y);
                continued = true;
            }

            NewPage();
            foreach (var stop in list)
            {
                if (y + RowHeight > FooterTop - 8)
                    NewPage();
                y = DrawRow(gfx!, y, stop);
            }
            gfx?.Dispose();
        }

        NumberPages(document);
        document.Save(path);
    }

    private static double DrawHeader(XGraphics gfx, byte[]? logo, string posteLine, string settingsLine, string summary, DateTime generated)
    {
        var textLeft = Margin;
        if (logo != null)
        {
            using var image = XImage.FromStream(() => new MemoryStream(logo));
            const double height = 44;
            var width = height * image.PointWidth / image.PointHeight;
            gfx.DrawImage(image, Margin, 14, width, height);
            textLeft = Margin + width + 14;
        }
        gfx.DrawString("Fiche des arrêts de production", TitleFont, XBrushes.Black,
            new XRect(textLeft, 14, 420, 20), XStringFormats.TopLeft);
        gfx.DrawString($"Généré le {generated:dd/MM/yyyy HH:mm}", SmallFont, XBrushes.DimGray,
            new XRect(Margin, 16, PageWidth - 2 * Margin, 10), XStringFormats.TopRight);
        gfx.DrawString(posteLine, InfoFont, XBrushes.Black, new XRect(textLeft, 38, PageWidth - textLeft - Margin, 14), XStringFormats.TopLeft);
        gfx.DrawLine(XPens.LightGray, Margin, 64, PageWidth - Margin, 64);
        gfx.DrawString(summary, FooterFont, XBrushes.Black, new XRect(Margin, 70, 400, 12), XStringFormats.TopLeft);
        gfx.DrawString(settingsLine, SmallFont, XBrushes.DimGray, new XRect(Margin, 72, PageWidth - 2 * Margin, 10), XStringFormats.TopRight);
        return 90;
    }

    private static double DrawTableHeader(XGraphics gfx, double y)
    {
        const double height = 18;
        var x = Margin;
        foreach (var (header, width) in Columns)
        {
            gfx.DrawRectangle(GridPen, HeaderFill, x, y, width, height);
            gfx.DrawString(header, HeaderFont, XBrushes.Black, new XRect(x + 3, y, width - 6, height), XStringFormats.CenterLeft);
            x += width;
        }
        return y + height;
    }

    private static double DrawRow(XGraphics gfx, double y, ProductionStop stop)
    {
        var cells = new[]
        {
            stop.MachineName,
            stop.Start.ToString("HH:mm"), stop.End.ToString("HH:mm"), stop.DurationText, stop.BreakText, stop.TypeLabel,
            "", "", "",
        };
        var x = Margin;
        for (var c = 0; c < Columns.Length; c++)
        {
            var width = Columns[c].Width;
            if (stop.IsMostlyNoData && c < 6)
                gfx.DrawRectangle(NoDataFill, x, y, width, RowHeight);
            gfx.DrawRectangle(GridPen, x, y, width, RowHeight);
            if (cells[c].Length > 0)
                gfx.DrawString(Fit(gfx, cells[c], width - 6), CellFont, XBrushes.Black,
                    new XRect(x + 3, y, width - 6, RowHeight), XStringFormats.CenterLeft);
            x += width;
        }
        return y + RowHeight;
    }

    private static void DrawFooter(XGraphics gfx)
    {
        gfx.DrawString("« Sans données » : StatsViewer n'a rien enregistré (PC ou réseau arrêté), la machine a pu tourner ou non.",
            SmallFont, XBrushes.DimGray, new XRect(Margin, FooterTop, PageWidth - 2 * Margin, 10), XStringFormats.TopLeft);
        gfx.DrawString("Chef d'équipe : ______________________________          Signature : ______________________________",
            FooterFont, XBrushes.Black, new XRect(Margin, FooterTop + 20, PageWidth - 2 * Margin, 14), XStringFormats.TopLeft);
    }

    private static void NumberPages(PdfDocument document)
    {
        var count = document.PageCount;
        for (var i = 0; i < count; i++)
        {
            using var gfx = XGraphics.FromPdfPage(document.Pages[i]);
            gfx.DrawString($"Page {i + 1} / {count}", SmallFont, XBrushes.DimGray,
                new XRect(Margin, PageHeight - 18, PageWidth - 2 * Margin, 10), XStringFormats.TopRight);
        }
    }

    // Cuts a cell's text to its column so neighbours never overlap.
    private static string Fit(XGraphics gfx, string text, double width)
    {
        if (gfx.MeasureString(text, CellFont).Width <= width)
            return text;
        while (text.Length > 1 && gfx.MeasureString(text + "…", CellFont).Width > width)
            text = text[..^1];
        return text + "…";
    }

    private static byte[]? LoadLogo()
    {
        try
        {
            using var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/SpigaDashboards.App;component/Assets/spiga-logo.png"))?.Stream;
            if (stream == null)
                return null;
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"Logo introuvable pour la fiche des arrets : {ex.Message}");
            return null;
        }
    }
}
