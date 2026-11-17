using System.Globalization;

namespace SpigaDashboards.App.Models;

// One Stiavelli production (ID_produzione) as of its latest snapshot.
// A production = one article run on one line: a new one is opened at each
// changeover. Counters are Stiavelli's own cumulative totals since StartTime.
public class ProductionInfo
{
    public string ProductionId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public DateTime StartTime { get; set; }
    public DateTime LastSnapshot { get; set; }
    public bool IsTerminated { get; set; }
    public long TotalBags { get; set; }
    public long BagsInTolerance { get; set; }
    public long BagsUnderTolerance { get; set; }
    public long BagsOverTolerance { get; set; }
    public double TargetNetWeight { get; set; }

    // Stiavelli's running average; null when obviously corrupt (see ProductionBucket).
    public double? AvgWeight { get; set; }

    public double QualityPercent => TotalBags > 0 ? Math.Round(100.0 * BagsInTolerance / TotalBags, 1) : 0;
}

// ID_produzione is CHAR(43) = line (4) + article code (20) + start time
// "dd.MM.yyyy-HH:mm:ss" (19), e.g.
//   CC01 | 00000000NOUVELLEPROD | 16.10.2024-10:07:47
// Matches numero_linea / codice_articolo / tempo_inizio on every row seen;
// used as the fallback when one of those columns is blank.
public readonly record struct ProductionIdParts(string Line, string ArticleCode, DateTime? StartTime)
{
    private const int LineLength = 4;
    private const int ArticleLength = 20;
    private const string StartFormat = "dd.MM.yyyy-HH:mm:ss";

    public static ProductionIdParts Parse(string? productionId)
    {
        var id = productionId ?? "";
        var line = id.Length >= LineLength ? id[..LineLength].Trim() : id.Trim();
        var article = id.Length >= LineLength + ArticleLength ? id.Substring(LineLength, ArticleLength).Trim() : "";
        DateTime? start = id.Length > LineLength + ArticleLength
            && DateTime.TryParseExact(id[(LineLength + ArticleLength)..].Trim(), StartFormat,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        return new ProductionIdParts(line, article, start);
    }

    // 00000000NOUVELLEPROD -> NOUVELLEPROD (all-zero codes are kept as-is).
    public static string DisplayArticle(string articleCode)
        => articleCode.Trim().TrimStart('0') is { Length: > 0 } trimmed ? trimmed : articleCode.Trim();
}
