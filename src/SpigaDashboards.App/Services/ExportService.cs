using System.Globalization;
using System.IO;
using ClosedXML.Excel;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView.SKCharts;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using SkiaSharp;
using SpigaDashboards.App.Models;
using SpigaDashboards.App.ViewModels;

namespace SpigaDashboards.App.Services;

// Excel/PDF export of the history browser (spec S6): a dashboard page (KPIs
// + charts), the breakdowns per machine / weight / team / time step, then the
// hour-by-hour detail. ClosedXML and PdfSharpCore are both MIT-licensed with
// no revenue-tier restriction (unlike some alternatives), so there's no
// licensing question for the client.
public static class ExportService
{
    // French number format (1 234,5) with a plain space as thousands
    // separator: .NET's fr-FR uses U+202F, which the PDF's Arial may not draw.
    private static readonly NumberFormatInfo Numbers = CreateNumbers();

    private static NumberFormatInfo CreateNumbers()
    {
        var format = (NumberFormatInfo)CultureInfo.GetCultureInfo("fr-FR").NumberFormat.Clone();
        format.NumberGroupSeparator = " ";
        return format;
    }

    private static string N0(double v) => v.ToString("N0", Numbers);
    private static string N1(double v) => v.ToString("N1", Numbers);
    private static string Pct(double? v) => v is { } p ? p.ToString("0.0", Numbers) + " %" : "—";
    private static string Grams(double? v) => v is { } g ? g.ToString("+0.0;-0.0;0.0", Numbers) + " g" : "—";
    private static string Speed(double? v) => v is { } s ? s.ToString("0.0", Numbers) : "—";

    // --- Charts -------------------------------------------------------------

    private sealed record ChartImage(string Title, byte[]? Png);

    // Same charts as the dashboard, drawn on white for paper / Excel.
    private static List<ChartImage> BuildCharts(ProductionReport report)
    {
        using var _ = ChartHelpers.PrintScope();
        var steps = report.ChartSteps;
        var by = "par " + PeriodRanges.GrainName(report.Grain);
        return new List<ChartImage>
        {
            new($"TRS {by} (cible {report.TrsTarget:0} %)", Render(DashboardCharts.Trs(steps, report.TrsTarget))),
            new($"Production nette {by}, par poids", Render(DashboardCharts.ProductionByWeight(steps))),
            new($"Vitesse moyenne / max {by} (sacs/min)", Render(DashboardCharts.Speed(steps))),
            new($"Écart de poids {by} (% de la cible)", Render(DashboardCharts.Deviation(steps))),
            new("Production nette par machine (kg)", Render(DashboardCharts.Bars(
                report.Machines.Select(m => m.MachineName).ToList(),
                report.Machines.Select(m => m.Kpis.ProductionKg).ToList(), "Production", v => ChartHelpers.Compact(v)))),
            new("TRS par équipe (%)", Render(DashboardCharts.Bars(
                report.Teams.Select(t => t.Label).ToList(),
                report.Teams.Select(t => t.Kpis.TrsPercent ?? 0).ToList(), "TRS", v => $"{v:0.0}"))),
        };
    }

    private static byte[]? Render(ChartParts parts, int width = 900, int height = 420)
    {
        if (!parts.HasData)
            return null;
        var chart = new SKCartesianChart
        {
            Width = width,
            Height = height,
            Background = SKColors.White,
            Series = parts.Series,
            XAxes = parts.XAxes,
            YAxes = parts.YAxes,
            LegendPosition = parts.Series.Length > 1 ? LegendPosition.Top : LegendPosition.Hidden,
            LegendTextPaint = parts.LegendTextPaint,
            LegendTextSize = 14,
        };
        using var image = chart.GetImage();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // --- Excel --------------------------------------------------------------

    public static void ExportToExcel(ProductionReport report, string path)
    {
        using var workbook = new XLWorkbook();
        var charts = BuildCharts(report);

        WriteDashboardSheet(workbook.Worksheets.Add("Tableau de bord"), report, charts);

        WriteTable(workbook.Worksheets.Add("Par machine"),
            new[] { "Machine", "Secteur", "TRS cible %", "TRS %", "Disponibilité %", "Performance %", "Qualité %",
                    "Vitesse moy. (sacs/min)", "Vitesse max (sacs/min)", "Sacs", "Production nette (kg)", "Écart (g)", "Écart (%)",
                    "Surpoids (kg)", "Conformes", "Sous tol.", "Sur tol.", "Rejets métal" },
            report.Machines.Select(m => new object?[]
            {
                m.MachineName, Machine.SectorLabel(m.Sector), m.TrsTarget, m.Kpis.TrsPercent, m.Kpis.AvailabilityPercent, m.Kpis.PerformancePercent,
                m.Kpis.QualityPercent, m.Kpis.SpeedBagsPerMinute, m.Kpis.MaxSpeedBagsPerMinute, m.Kpis.TotalBags, m.Kpis.ProductionKg,
                m.Kpis.WeightDeviationGrams, m.Kpis.WeightDeviationPercent, m.Kpis.GiveawayKg, m.Kpis.BagsInTolerance,
                m.Kpis.BagsUnderTolerance, m.Kpis.BagsOverTolerance, m.Kpis.MetalRejects,
            }));

        WriteTable(workbook.Worksheets.Add("Par poids"),
            new[] { "Machine", "Poids cible (g)", "Produit", "Sachet vide (g)", "Cadence nominale (sacs/min)", "TRS %", "Sacs",
                    "Production nette (kg)", "Poids moyen net (g)", "Écart (g)", "Écart (%)", "Surpoids (kg)", "Conformité %",
                    "Vitesse moy. (sacs/min)", "Vitesse max (sacs/min)" },
            report.Weights.Select(w => new object?[]
            {
                w.MachineName, w.Weight.TargetWeight, w.Weight.ProductName, w.TareGrams, w.NominalRate > 0 ? w.NominalRate : null,
                w.Weight.TrsPercent, w.Weight.TotalBags, w.Weight.ProductionKg, w.Weight.AvgWeight, w.Weight.DeviationGrams,
                w.Weight.DeviationPercent, w.Weight.GiveawayKg, w.Weight.QualityPercent, w.Weight.SpeedBagsPerMinute,
                w.Weight.MaxSpeedBagsPerMinute,
            }));

        var groupHeaders = new[] { "TRS %", "Disponibilité %", "Performance %", "Qualité %", "Vitesse moy. (sacs/min)",
            "Vitesse max (sacs/min)", "Sacs", "Production nette (kg)", "Poids moyen net (g)", "Écart (g)", "Surpoids (kg)" };
        WriteTable(workbook.Worksheets.Add("Par équipe"), new[] { "Équipe" }.Concat(groupHeaders).ToArray(),
            report.Teams.Select(GroupCells));
        WriteTable(workbook.Worksheets.Add("Par " + PeriodRanges.GrainName(report.Grain)),
            new[] { char.ToUpper(PeriodRanges.GrainName(report.Grain)[0]) + PeriodRanges.GrainName(report.Grain)[1..] }.Concat(groupHeaders).ToArray(),
            report.Steps.Select(GroupCells));

        var detail = workbook.Worksheets.Add("Détail horaire");
        WriteTable(detail,
            new[] { "Date/Heure", "Machine", "Secteur", "Poste", "Équipe", "Produit", "Poids cible (g)", "Sachet vide (g)",
                    "Poids moyen net (g)", "Écart (g)", "Sacs", "Production nette (kg)", "Conformes", "Sur tol.", "Sous tol.",
                    "Vitesse moy.", "Vitesse max", "Disponibilité %", "TRS %", "Conforme" },
            report.Detail.Select(r => new object?[]
            {
                r.StartTime, r.MachineName, Machine.SectorLabel(r.Sector), r.ShiftName, r.Team, r.ProductName, r.TargetNetWeight, r.TareGrams,
                r.AvgWeight, r.DeviationGrams, r.TotalBags, r.ProductionKg, r.BagsInTolerance, r.BagsOverTolerance,
                r.BagsUnderTolerance, r.SpeedBagsPerMinute, r.MaxSpeedBagsPerMinute, r.AvailabilityPercent, r.TrsPercent,
                r.IsConform ? "Oui" : "Non",
            }));
        detail.Column(1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
        detail.Column(1).AdjustToContents();

        workbook.SaveAs(path);
    }

    private static object?[] GroupCells(GroupReportRow g) => new object?[]
    {
        g.Label, g.Kpis.TrsPercent, g.Kpis.AvailabilityPercent, g.Kpis.PerformancePercent, g.Kpis.QualityPercent,
        g.Kpis.SpeedBagsPerMinute, g.Kpis.MaxSpeedBagsPerMinute, g.Kpis.TotalBags, g.Kpis.ProductionKg,
        SingleAverage(g.Kpis),
        g.Kpis.WeightDeviationGrams, g.Kpis.GiveawayKg,
    };

    // Average weight only means something for a single target weight.
    private static double? SingleAverage(KpiSummary kpis) => kpis.Weights.Count == 1 ? kpis.Weights[0].AvgWeight : null;

    private static void WriteDashboardSheet(IXLWorksheet sheet, ProductionReport report, List<ChartImage> charts)
    {
        sheet.Cell(1, 1).Value = "SPIGA - Rapport de production";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 16;
        sheet.Cell(2, 1).Value = "Période : " + report.PeriodText;
        sheet.Cell(3, 1).Value = "Filtres : " + (report.FilterLabel.Length == 0 ? "aucun" : report.FilterLabel);
        sheet.Cell(4, 1).Value = $"Généré le {report.GeneratedAt:dd/MM/yyyy HH:mm}";

        var row = 6;
        sheet.Cell(row, 1).Value = "Indicateur";
        sheet.Cell(row, 2).Value = "Valeur";
        sheet.Range(row, 1, row, 2).Style.Font.Bold = true;
        sheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");
        foreach (var (label, value) in KpiLines(report))
        {
            row++;
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 2).Value = value;
        }
        sheet.Column(1).Width = 30;
        sheet.Column(2).Width = 22;

        // Charts in two columns to the right of the KPI table.
        for (var i = 0; i < charts.Count; i++)
        {
            var anchorRow = 2 + i / 2 * 23;
            var anchorCol = 4 + i % 2 * 10;
            sheet.Cell(anchorRow, anchorCol).Value = charts[i].Title;
            sheet.Cell(anchorRow, anchorCol).Style.Font.Bold = true;
            if (charts[i].Png is not { } png)
            {
                sheet.Cell(anchorRow + 1, anchorCol).Value = "Pas de données";
                continue;
            }
            using var stream = new MemoryStream(png);
            sheet.AddPicture(stream).MoveTo(sheet.Cell(anchorRow + 1, anchorCol)).WithSize(600, 280);
        }
    }

    private static IEnumerable<(string Label, string Value)> KpiLines(ProductionReport report)
    {
        var k = report.Summary;
        yield return ("TRS", Pct(k.TrsPercent) + $" (cible {report.TrsTarget:0} %)");
        yield return ("Disponibilité", Pct(k.AvailabilityPercent));
        yield return ("Performance", Pct(k.PerformancePercent));
        yield return ("Qualité (conformité)", Pct(k.QualityPercent));
        yield return ("Vitesse moyenne", Speed(k.SpeedBagsPerMinute) + " sacs/min");
        yield return ("Vitesse max atteinte", Speed(k.MaxSpeedBagsPerMinute) + " sacs/min");
        yield return ("Sacs produits", N0(k.TotalBags));
        yield return ("Production nette", KpiCalculator.FormatMass(k.ProductionKg));
        yield return ("Poids moyen net", KpiCalculator.FormatAverages(k.Weights));
        yield return ("Écart de poids", Grams(k.WeightDeviationGrams) + (k.WeightDeviationPercent is { } p ? $" ({p.ToString("+0.00;-0.00;0.00", Numbers)} %)" : ""));
        yield return ("Surpoids donné", k.GiveawayKg is { } g ? KpiCalculator.FormatMass(g, signed: true) : "—");
        yield return ("Sacs conformes", N0(k.BagsInTolerance));
        yield return ("Sacs sous tolérance", N0(k.BagsUnderTolerance));
        yield return ("Sacs sur tolérance", N0(k.BagsOverTolerance));
        yield return ("Rejets détecteur de métaux", N0(k.MetalRejects));
        yield return ("Poids produits", k.Weights.Count == 0 ? "—" : string.Join(", ", k.Weights.Select(w => $"{w.TargetWeight:0} g")));
    }

    private static void WriteTable(IXLWorksheet sheet, string[] headers, IEnumerable<object?[]> rows)
    {
        for (var c = 0; c < headers.Length; c++)
            sheet.Cell(1, c + 1).Value = headers[c];
        var header = sheet.Range(1, 1, 1, headers.Length);
        header.Style.Font.Bold = true;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCE6F1");

        var r = 2;
        foreach (var values in rows)
        {
            for (var c = 0; c < values.Length; c++)
            {
                var cell = sheet.Cell(r, c + 1);
                // Unknown values stay blank, never 0.
                switch (values[c])
                {
                    case null:
                        break;
                    case DateTime date:
                        cell.Value = date;
                        break;
                    case string text:
                        cell.Value = text;
                        break;
                    case double d:
                        cell.Value = Math.Round(d, 2);
                        cell.Style.NumberFormat.Format = "#,##0.0#";
                        break;
                    case long l:
                        cell.Value = l;
                        cell.Style.NumberFormat.Format = "#,##0";
                        break;
                    default:
                        cell.Value = values[c]!.ToString();
                        break;
                }
            }
            r++;
        }

        if (r > 2)
            sheet.Range(1, 1, r - 1, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(1);
        sheet.Columns().AdjustToContents(1, Math.Min(r, 500));
    }

    // --- PDF ----------------------------------------------------------------

    private const double PageWidth = 842, PageHeight = 595; // A4 landscape, in points
    private const double Margin = 30;

    private static readonly XFont TitleFont = new("Arial", 16, XFontStyle.Bold);
    private static readonly XFont SubtitleFont = new("Arial", 9, XFontStyle.Regular);
    private static readonly XFont SectionFont = new("Arial", 12, XFontStyle.Bold);
    private static readonly XFont BoxLabelFont = new("Arial", 8, XFontStyle.Regular);
    private static readonly XFont BoxValueFont = new("Arial", 14, XFontStyle.Bold);
    private static readonly XFont HeaderFont = new("Arial", 7.5, XFontStyle.Bold);
    private static readonly XFont CellFont = new("Arial", 7.5, XFontStyle.Regular);
    private static readonly XBrush HeaderFill = new XSolidBrush(XColor.FromArgb(0xDC, 0xE6, 0xF1));
    private static readonly XBrush BoxFill = new XSolidBrush(XColor.FromArgb(0xF3, 0xF5, 0xF8));

    public static void ExportToPdf(ProductionReport report, string path)
    {
        var document = new PdfDocument();
        var pdf = new PdfWriter(document, report);
        var charts = BuildCharts(report);

        // Page 1: KPI boxes + the four trend charts.
        pdf.NewPage();
        var k = report.Summary;
        var boxes = new (string, string)[]
        {
            ("TRS (cible " + report.TrsTarget.ToString("0", Numbers) + " %)", Pct(k.TrsPercent)),
            ("Disponibilité", Pct(k.AvailabilityPercent)),
            ("Performance", Pct(k.PerformancePercent)),
            ("Qualité", Pct(k.QualityPercent)),
            ("Vitesse moy. / max", Speed(k.SpeedBagsPerMinute) + " / " + Speed(k.MaxSpeedBagsPerMinute)),
            ("Sacs produits", N0(k.TotalBags)),
            ("Production nette", KpiCalculator.FormatMass(k.ProductionKg)),
            ("Poids moyen net", KpiCalculator.FormatAverages(k.Weights)),
            ("Écart de poids", Grams(k.WeightDeviationGrams)),
            ("Surpoids donné", k.GiveawayKg is { } g ? KpiCalculator.FormatMass(g, signed: true) : "—"),
            ("Sous / sur tolérance", N0(k.BagsUnderTolerance) + " / " + N0(k.BagsOverTolerance)),
            ("Rejets métal", N0(k.MetalRejects)),
        };
        pdf.Boxes(boxes, perRow: 6);
        pdf.ChartGrid(charts.Take(4).ToList(), columns: 2, height: 175);

        // Page 2: production per machine / team, then the breakdown tables.
        pdf.NewPage();
        pdf.ChartGrid(charts.Skip(4).ToList(), columns: 2, height: 165);
        pdf.Table("Par machine",
            new[] { "Machine", "Secteur", "TRS", "Cible", "Dispo.", "Perf.", "Qualité", "Vit. moy.", "Vit. max", "Sacs", "Production", "Écart", "Surpoids" },
            new double[] { 85, 55, 50, 40, 50, 50, 50, 50, 50, 65, 70, 55, 60 },
            report.Machines.Select(m => new[]
            {
                m.MachineName, Machine.SectorLabel(m.Sector), Pct(m.Kpis.TrsPercent), m.TrsTarget.ToString("0", Numbers) + " %", Pct(m.Kpis.AvailabilityPercent),
                Pct(m.Kpis.PerformancePercent), Pct(m.Kpis.QualityPercent), Speed(m.Kpis.SpeedBagsPerMinute),
                Speed(m.Kpis.MaxSpeedBagsPerMinute), N0(m.Kpis.TotalBags), KpiCalculator.FormatMass(m.Kpis.ProductionKg),
                Grams(m.Kpis.WeightDeviationGrams), m.Kpis.GiveawayKg is { } kg ? KpiCalculator.FormatMass(kg, signed: true) : "—",
            }));
        pdf.Table("Par poids (sachet vide déduit)",
            new[] { "Machine", "Poids", "Produit", "Sachet", "Cadence", "TRS", "Sacs", "Production", "Poids moyen", "Écart", "Écart %", "Surpoids", "Conformité" },
            new double[] { 75, 45, 75, 45, 50, 50, 60, 65, 60, 50, 50, 60, 55 },
            report.Weights.Select(w => new[]
            {
                w.MachineName, w.Weight.TargetWeight.ToString("0", Numbers) + " g", w.Weight.ProductName,
                w.TareGrams.ToString("0.#", Numbers) + " g", w.NominalRate > 0 ? w.NominalRate.ToString("0.#", Numbers) : "—",
                Pct(w.Weight.TrsPercent), N0(w.Weight.TotalBags), w.Weight.ProductionText,
                w.Weight.AvgWeight is { } a ? N1(a) + " g" : "—", Grams(w.Weight.DeviationGrams),
                w.Weight.DeviationPercent is { } p ? p.ToString("+0.00;-0.00;0.00", Numbers) + " %" : "—",
                w.Weight.GiveawayText, Pct(w.Weight.QualityPercent),
            }));
        var groupHeaders = new[] { "TRS", "Dispo.", "Perf.", "Qualité", "Vit. moy.", "Vit. max", "Sacs", "Production", "Écart", "Surpoids" };
        var groupWidths = new double[] { 110, 55, 55, 55, 55, 55, 55, 70, 75, 60, 65 };
        pdf.Table("Par équipe", new[] { "Équipe" }.Concat(groupHeaders).ToArray(), groupWidths, report.Teams.Select(GroupRow));
        pdf.Table("Par " + PeriodRanges.GrainName(report.Grain), new[] { "Période" }.Concat(groupHeaders).ToArray(), groupWidths,
            report.Steps.Where(s => s.Kpis.TotalBags > 0).Select(GroupRow));

        // Detail: one row per machine, hour and production.
        pdf.NewPage();
        pdf.Table($"Détail horaire ({N0(report.Detail.Count)} lignes : une par machine, heure et production)",
            new[] { "Date/Heure", "Machine", "Poste", "Équipe", "Produit", "Cible", "Net moyen", "Écart", "Sacs", "Prod. kg",
                    "Vit. moy.", "Vit. max", "Dispo.", "TRS", "Conf.", "Sur tol.", "Sous tol." },
            new double[] { 62, 60, 34, 42, 60, 34, 44, 38, 42, 46, 38, 38, 36, 38, 42, 36, 36 },
            report.Detail.Select(r => new[]
            {
                r.StartTime.ToString("dd/MM/yy HH:mm"), r.MachineName, r.ShiftName, r.Team, r.ProductName,
                N0(r.TargetNetWeight), r.AvgWeight is { } a ? N1(a) : "", r.DeviationGrams is { } d ? d.ToString("+0.0;-0.0;0.0", Numbers) : "",
                N0(r.TotalBags), N1(r.ProductionKg), Speed(r.SpeedBagsPerMinute), Speed(r.MaxSpeedBagsPerMinute),
                r.AvailabilityPercent.ToString("0", Numbers) + "%", r.TrsPercent is { } t ? t.ToString("0", Numbers) + "%" : "—",
                N0(r.BagsInTolerance), N0(r.BagsOverTolerance), N0(r.BagsUnderTolerance),
            }));

        pdf.Finish();
        document.Save(path);
    }

    private static string[] GroupRow(GroupReportRow g) => new[]
    {
        g.Label, Pct(g.Kpis.TrsPercent), Pct(g.Kpis.AvailabilityPercent), Pct(g.Kpis.PerformancePercent), Pct(g.Kpis.QualityPercent),
        Speed(g.Kpis.SpeedBagsPerMinute), Speed(g.Kpis.MaxSpeedBagsPerMinute), N0(g.Kpis.TotalBags),
        KpiCalculator.FormatMass(g.Kpis.ProductionKg), Grams(g.Kpis.WeightDeviationGrams),
        g.Kpis.GiveawayKg is { } kg ? KpiCalculator.FormatMass(kg, signed: true) : "—",
    };

    // Page flow for the PDF: header on every page, page numbers at the end,
    // tables that continue (with their header) on the next page.
    private sealed class PdfWriter
    {
        private readonly PdfDocument _document;
        private readonly ProductionReport _report;
        private XGraphics _gfx = null!;
        private double _y;

        public PdfWriter(PdfDocument document, ProductionReport report)
        {
            _document = document;
            _report = report;
        }

        public void NewPage()
        {
            _gfx?.Dispose();
            var page = _document.AddPage();
            page.Width = XUnit.FromPoint(PageWidth);
            page.Height = XUnit.FromPoint(PageHeight);
            _gfx = XGraphics.FromPdfPage(page);
            _gfx.DrawString("SPIGA - Rapport de production", TitleFont, XBrushes.Black, new XRect(Margin, 18, 500, 20), XStringFormats.TopLeft);
            var subtitle = $"Période {_report.PeriodText}  -  Filtres : {(_report.FilterLabel.Length == 0 ? "aucun" : _report.FilterLabel)}"
                           + $"  -  Généré le {_report.GeneratedAt:dd/MM/yyyy HH:mm}";
            _gfx.DrawString(subtitle, SubtitleFont, XBrushes.DimGray, new XRect(Margin, 38, PageWidth - 2 * Margin, 12), XStringFormats.TopLeft);
            _gfx.DrawLine(XPens.LightGray, Margin, 54, PageWidth - Margin, 54);
            _y = 62;
        }

        private void EnsureSpace(double height)
        {
            if (_y + height > PageHeight - Margin)
                NewPage();
        }

        public void Boxes(IReadOnlyList<(string Label, string Value)> boxes, int perRow)
        {
            const double gap = 6, height = 40;
            var width = (PageWidth - 2 * Margin - (perRow - 1) * gap) / perRow;
            for (var i = 0; i < boxes.Count; i++)
            {
                if (i % perRow == 0 && i > 0)
                    _y += height + gap;
                var x = Margin + i % perRow * (width + gap);
                _gfx.DrawRoundedRectangle(BoxFill, x, _y, width, height, 6, 6);
                _gfx.DrawString(boxes[i].Label, BoxLabelFont, XBrushes.DimGray, new XRect(x + 6, _y + 4, width - 12, 10), XStringFormats.TopLeft);
                var font = boxes[i].Value.Length > 16 ? new XFont("Arial", 10, XFontStyle.Bold) : BoxValueFont;
                _gfx.DrawString(boxes[i].Value, font, XBrushes.Black, new XRect(x + 6, _y + 17, width - 12, 20), XStringFormats.TopLeft);
            }
            _y += height + 12;
        }

        public void ChartGrid(IReadOnlyList<ChartImage> charts, int columns, double height)
        {
            const double gap = 12;
            var width = (PageWidth - 2 * Margin - (columns - 1) * gap) / columns;
            for (var i = 0; i < charts.Count; i += columns)
            {
                EnsureSpace(height + 16);
                for (var c = 0; c < columns && i + c < charts.Count; c++)
                {
                    var chart = charts[i + c];
                    var x = Margin + c * (width + gap);
                    _gfx.DrawString(chart.Title, HeaderFont, XBrushes.Black, new XRect(x, _y, width, 10), XStringFormats.TopLeft);
                    if (chart.Png is { } png)
                    {
                        using var image = XImage.FromStream(() => new MemoryStream(png));
                        // Keep the image's aspect ratio inside the cell.
                        var scale = Math.Min(width / image.PointWidth, height / image.PointHeight);
                        _gfx.DrawImage(image, x, _y + 12, image.PointWidth * scale, image.PointHeight * scale);
                    }
                    else
                    {
                        _gfx.DrawString("Pas de données", CellFont, XBrushes.Gray, new XRect(x, _y + 12 + height / 2, width, 10), XStringFormats.TopCenter);
                    }
                }
                _y += height + 16;
            }
        }

        public void Table(string title, string[] headers, double[] widths, IEnumerable<string[]> rows)
        {
            const double rowHeight = 13;
            var totalWidth = widths.Sum();
            var scale = Math.Min(1, (PageWidth - 2 * Margin) / totalWidth);
            var cols = widths.Select(w => w * scale).ToArray();

            void Header()
            {
                _gfx.DrawRectangle(HeaderFill, Margin, _y, cols.Sum(), rowHeight);
                var x = Margin;
                for (var c = 0; c < headers.Length; c++)
                {
                    _gfx.DrawString(headers[c], HeaderFont, XBrushes.Black, new XRect(x + 2, _y + 2, cols[c] - 4, rowHeight), XStringFormats.TopLeft);
                    x += cols[c];
                }
                _y += rowHeight;
            }

            EnsureSpace(20 + 3 * rowHeight);
            _gfx.DrawString(title, SectionFont, XBrushes.Black, new XRect(Margin, _y, PageWidth - 2 * Margin, 16), XStringFormats.TopLeft);
            _y += 18;
            Header();
            var any = false;
            foreach (var row in rows)
            {
                any = true;
                if (_y + rowHeight > PageHeight - Margin)
                {
                    NewPage();
                    Header();
                }
                var x = Margin;
                for (var c = 0; c < headers.Length && c < row.Length; c++)
                {
                    _gfx.DrawString(Fit(row[c], cols[c] - 4), CellFont, XBrushes.Black, new XRect(x + 2, _y + 2, cols[c] - 4, rowHeight), XStringFormats.TopLeft);
                    x += cols[c];
                }
                _y += rowHeight;
                _gfx.DrawLine(XPens.Gainsboro, Margin, _y, Margin + cols.Sum(), _y);
            }
            if (!any)
            {
                _gfx.DrawString("Aucune donnée", CellFont, XBrushes.Gray, new XRect(Margin + 2, _y + 2, 200, rowHeight), XStringFormats.TopLeft);
                _y += rowHeight;
            }
            _y += 14;
        }

        // Cuts a cell's text to its column so neighbours never overlap.
        private string Fit(string text, double width)
        {
            if (_gfx.MeasureString(text, CellFont).Width <= width)
                return text;
            while (text.Length > 1 && _gfx.MeasureString(text + "…", CellFont).Width > width)
                text = text[..^1];
            return text + "…";
        }

        public void Finish()
        {
            _gfx?.Dispose();
            var count = _document.PageCount;
            for (var i = 0; i < count; i++)
            {
                using var gfx = XGraphics.FromPdfPage(_document.Pages[i]);
                gfx.DrawString($"Page {i + 1} / {count}", SubtitleFont, XBrushes.DimGray,
                    new XRect(Margin, PageHeight - 20, PageWidth - 2 * Margin, 10), XStringFormats.TopRight);
            }
        }
    }
}
