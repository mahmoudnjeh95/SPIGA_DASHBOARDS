using ClosedXML.Excel;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App.Services;

// Excel/PDF export for the history browser (spec S6). ClosedXML and
// PdfSharpCore are both MIT-licensed with no revenue-tier restriction
// (unlike some alternatives), so there's no licensing question for the client.
public static class ExportService
{
    private static readonly string[] ColumnHeaders =
    {
        "Date/Heure", "Machine", "Poste", "Equipe", "Produit", "Poids cible (g)", "Poids moyen (g)",
        "Total sacs", "Conformes", "Eleves", "Faibles", "Conforme",
    };

    public static void ExportToExcel(IReadOnlyList<HistoryRow> rows, string path)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Historique");

        for (var i = 0; i < ColumnHeaders.Length; i++)
            sheet.Cell(1, i + 1).Value = ColumnHeaders[i];
        sheet.Row(1).Style.Font.Bold = true;

        var row = 2;
        foreach (var r in rows)
        {
            sheet.Cell(row, 1).Value = r.StartTime;
            sheet.Cell(row, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            sheet.Cell(row, 2).Value = r.MachineId;
            sheet.Cell(row, 3).Value = r.ShiftName;
            sheet.Cell(row, 4).Value = r.Team;
            sheet.Cell(row, 5).Value = r.ProductName;
            sheet.Cell(row, 6).Value = r.TargetNetWeight;
            sheet.Cell(row, 7).Value = r.AvgWeight;
            sheet.Cell(row, 8).Value = r.TotalBags;
            sheet.Cell(row, 9).Value = r.BagsInTolerance;
            sheet.Cell(row, 10).Value = r.BagsOverTolerance;
            sheet.Cell(row, 11).Value = r.BagsUnderTolerance;
            sheet.Cell(row, 12).Value = r.IsConform ? "Oui" : "Non";
            row++;
        }

        sheet.Columns().AdjustToContents();
        workbook.SaveAs(path);
    }

    private static readonly string[] PdfHeaders =
    {
        "Date/Heure", "Machine", "Poste", "Equipe", "Produit", "Cible(g)", "Moyen(g)",
        "Sacs", "Conf.", "Elev.", "Faib.", "OK",
    };

    private static readonly double[] PdfColumnWidths = { 85, 50, 45, 60, 65, 45, 45, 40, 40, 40, 40, 30 };

    public static void ExportToPdf(KpiSummary summary, IReadOnlyList<HistoryRow> rows, string path)
    {
        var document = new PdfDocument();
        var titleFont = new XFont("Arial", 16, XFontStyle.Bold);
        var headerFont = new XFont("Arial", 9, XFontStyle.Bold);
        var cellFont = new XFont("Arial", 8, XFontStyle.Regular);
        var summaryFont = new XFont("Arial", 10, XFontStyle.Regular);

        const double marginTop = 40, marginLeft = 30, rowHeight = 16, marginBottom = 40;
        const double pageWidth = 842, pageHeight = 595; // A4 landscape, in points

        PdfPage page = null!;
        XGraphics gfx = null!;
        double y = 0;

        void DrawHeaderRow()
        {
            var x = marginLeft;
            foreach (var (header, width) in PdfHeaders.Zip(PdfColumnWidths))
            {
                gfx.DrawString(header, headerFont, XBrushes.Black, new XRect(x, y, width, rowHeight), XStringFormats.TopLeft);
                x += width;
            }
            y += rowHeight;
            gfx.DrawLine(XPens.Black, marginLeft, y, marginLeft + PdfColumnWidths.Sum(), y);
            y += 4;
        }

        void StartPage()
        {
            page = document.AddPage();
            page.Width = XUnit.FromPoint(pageWidth);
            page.Height = XUnit.FromPoint(pageHeight);
            gfx = XGraphics.FromPdfPage(page);
            y = marginTop;
            DrawHeaderRow();
        }

        StartPage();

        gfx.DrawString(
            $"Historique de production -- {rows.Count} lot(s) -- "
            + $"Total sacs: {summary.TotalBags}  Conformes: {summary.BagsInTolerance}  Eleves: {summary.BagsOverTolerance}  "
            + $"Faibles: {summary.BagsUnderTolerance}  Taux conformite: {summary.QualityPercent}%",
            summaryFont, XBrushes.Black, new XRect(marginLeft, 8, pageWidth - 2 * marginLeft, 24), XStringFormats.TopLeft);

        foreach (var row in rows)
        {
            if (y > pageHeight - marginBottom)
                StartPage();

            var values = new[]
            {
                row.StartTime.ToString("dd/MM/yy HH:mm"), row.MachineId, row.ShiftName, row.Team, row.ProductName,
                row.TargetNetWeight.ToString("0"), row.AvgWeight.ToString("0.0"), row.TotalBags.ToString(),
                row.BagsInTolerance.ToString(), row.BagsOverTolerance.ToString(), row.BagsUnderTolerance.ToString(),
                row.IsConform ? "Oui" : "Non",
            };

            var x = marginLeft;
            foreach (var (value, width) in values.Zip(PdfColumnWidths))
            {
                gfx.DrawString(value, cellFont, XBrushes.Black, new XRect(x, y, width, rowHeight), XStringFormats.TopLeft);
                x += width;
            }
            y += rowHeight;
        }

        document.Save(path);
    }
}
