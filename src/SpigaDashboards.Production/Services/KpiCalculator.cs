using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

public static class KpiCalculator
{
    private static readonly Dictionary<int, string> ShiftNames = new()
    {
        { 1, "Matin" },
        { 2, "Après-midi" },
        { 3, "Nuit" },
    };

    public static string ShiftName(int shiftId) => ShiftNames.GetValueOrDefault(shiftId, $"Poste {shiftId}");

    public static int ShiftStartHour(int shiftId) => shiftId switch
    {
        1 => 6,
        2 => 14,
        _ => 22,
    };

    public static string ShiftTimeRange(int shiftId) => shiftId switch
    {
        1 => "06h - 14h",
        2 => "14h - 22h",
        _ => "22h - 06h",
    };

    // Postes per client spec: Matin 06-14, Après-midi 14-22, Nuit 22-06 (wraps midnight).
    public static int CurrentShiftId(DateTime now) => now.Hour switch
    {
        < 6 => 3,
        < 14 => 1,
        < 22 => 2,
        _ => 3,
    };

    // TRS (OEE) = temps utile / temps requis (NF E60-182), = Disponibilite x
    // Performance x Qualite on one line at one rate. `lots` are hour/day
    // buckets (see ProductionBucket):
    //  - temps requis: observed time minus the planned stops the line was
    //    stopped in (daily breaks, Planification maintenance slots and
    //    "Planifié" stops; see ProductionRepository.QueryBucketsAsync);
    //  - temps utile: bags in tolerance / the production's nominal rate.
    //    Both are summed over every bucket before dividing, so a cumul of
    //    several lines, teams or days is never an average of their TRS.
    //    Productions with no known rate are left out of both.
    //  - Disponibilite: share of the required time in which the line
    //    produced (passed in, see AvailabilityPercent).
    //  - Performance: minutes the bags would have taken at each production's
    //    own nominal rate / producing minutes they actually took. Productions
    //    with no known rate are left out of both sides; none at all -> null
    //    (and TRS null) rather than a misleading 0 %. Not clamped: > 100 %
    //    means the nominal rate is set too low (flagged orange on screen).
    //  - Qualite: bags in tolerance / total bags.
    // Also breaks the buckets down per target weight (Weights), with each
    // weight's TRS banded against trsTarget and its deviation against
    // weightBand (the machine's), and totals the net weight made.
    public static KpiSummary Summarize(
        IReadOnlyCollection<ProductionBucket> lots, double availabilityPercent, TrsBand? trsBand = null, WeightBand? weightBand = null)
    {
        var summary = SummarizeCore(lots, availabilityPercent);
        summary.Weights = ByWeight(lots, trsBand, weightBand);
        summary.ProductionKg = Math.Round(summary.Weights.Sum(w => w.ProductionKg), 1);
        var weighed = summary.Weights.Where(w => w.GiveawayKg.HasValue).ToList();
        summary.GiveawayKg = weighed.Count > 0 ? Math.Round(weighed.Sum(w => w.GiveawayKg!.Value), 1) : null;
        return summary;
    }

    // One entry per target weight (rounded to the gram), biggest production
    // first. Production = bags x net average weight, or x target when that
    // weight has no trustworthy weight data; giveaway = bags x deviation.
    public static List<WeightKpi> ByWeight(
        IReadOnlyCollection<ProductionBucket> lots, TrsBand? trsBand = null, WeightBand? weightBand = null)
    {
        return lots
            .Where(l => l.TargetNetWeight > 0)
            .GroupBy(l => Math.Round(l.TargetNetWeight))
            .Select(g =>
            {
                var group = g.ToList();
                var k = SummarizeCore(group, AvailabilityPercent(group));
                var avg = WeightedAverageWeight(group);
                var product = group.GroupBy(l => l.ProductName ?? "")
                    .OrderByDescending(p => p.Sum(l => l.TotalBags))
                    .Select(p => p.Key)
                    .FirstOrDefault() ?? "";
                return new WeightKpi
                {
                    TargetWeight = g.Key,
                    ProductName = product,
                    TotalBags = k.TotalBags,
                    AvgWeight = avg is { } a ? Math.Round(a, 1) : null,
                    DeviationGrams = k.WeightDeviationGrams,
                    DeviationPercent = k.WeightDeviationPercent,
                    ProductionKg = Math.Round(k.TotalBags * g.Key / 1000.0, 1),
                    GiveawayKg = k.WeightDeviationGrams is { } dev ? Math.Round(k.TotalBags * dev / 1000.0, 1) : null,
                    QualityPercent = k.QualityPercent,
                    TrsPercent = k.TrsPercent,
                    SpeedBagsPerMinute = k.SpeedBagsPerMinute,
                    MaxSpeedBagsPerMinute = k.MaxSpeedBagsPerMinute,
                    DeviationSeverity = WeightDeviationSeverity(k.WeightDeviationPercent, weightBand ?? WeightBand.Default),
                    TrsSeverity = TrsSeverity(k.TrsPercent, trsBand ?? TrsBand.Default),
                };
            })
            .Where(w => w.TotalBags > 0 || w.AvgWeight.HasValue)
            .OrderByDescending(w => w.ProductionKg)
            .ToList();
    }

    // Colour band of a TRS against the machine's target and tolerance
    // (Parametres > Machines / Couleurs, see TrsBand).
    public static KpiSeverity TrsSeverity(double? trsPercent, TrsBand band) => band.Severity(trsPercent);

    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    // "850 kg", "12,4 t" (plain spaces: the PDF font may lack U+202F).
    public static string FormatMass(double kg, bool signed = false)
    {
        var sign = signed && kg > 0 ? "+" : "";
        var text = Math.Abs(kg) >= 10_000
            ? (kg / 1000).ToString("N1", French) + " t"
            : kg.ToString(Math.Abs(kg) >= 100 ? "N0" : "N1", French) + " kg";
        return sign + text.Replace(' ', ' ').Replace(' ', ' ');
    }

    // Average net weights of the weights produced, e.g. "501,2 g" or
    // "501,2 g · 1 003,5 g"; "—" when none is known.
    public static string FormatAverages(IEnumerable<WeightKpi> weights)
    {
        var known = weights.Where(w => w.AvgWeight.HasValue).Select(w => w.AvgWeight!.Value.ToString("N1", French) + " g").ToList();
        return known.Count == 0 ? "—" : string.Join(" · ", known).Replace(' ', ' ').Replace(' ', ' ');
    }

    // Vitesse moy. = bags / minute of steady running: the snapshot intervals
    // in which a line stops or restarts are partly stopped and would drag the
    // speed under the machine's real one. A period without any steady interval
    // (a run of a minute or two) falls back to bags / producing minute.
    public static double? AverageSpeed(IReadOnlyCollection<ProductionBucket> lots)
    {
        var steady = lots.Sum(l => l.SteadyMinutes);
        if (steady > 0)
            return lots.Sum(l => l.SteadyBags) / steady;
        var run = lots.Sum(l => l.RunMinutes);
        return run > 0 ? lots.Sum(l => l.TotalBags) / run : null;
    }

    private static KpiSummary SummarizeCore(IReadOnlyCollection<ProductionBucket> lots, double availabilityPercent)
    {
        // Weight comes from lots / sane running averages, which can sit in a
        // bucket with no counter delta (lot closed just after the snapshot).
        var (deviationGrams, deviationPercent) = WeightedDeviation(lots);

        var producing = lots.Where(l => l.IsProducing).ToList();
        if (producing.Count == 0)
        {
            return new KpiSummary
            {
                AvailabilityPercent = Math.Round(availabilityPercent, 1),
                WeightDeviationGrams = deviationGrams,
                WeightDeviationPercent = deviationPercent,
            };
        }

        var totalBags = producing.Sum(l => (long)l.TotalBags);
        var actualRate = AverageSpeed(producing) ?? 0;
        var ratedRunMinutes = producing.Sum(l => l.RatedRunMinutes);
        var idealMinutes = producing.Sum(l => l.IdealMinutes);
        double? performancePercent = ratedRunMinutes > 0 ? 100.0 * idealMinutes / ratedRunMinutes : null;

        // Qualite = temps utile / temps net (bags in tolerance / bags on one
        // line at one rate), so that D x P x Q is exactly the TRS below;
        // bags in tolerance / bags when no rate is known.
        var usefulMinutes = lots.Sum(l => l.UsefulMinutes);
        var qualityPercent = idealMinutes > 0 ? 100.0 * usefulMinutes / idealMinutes
            : totalBags > 0 ? 100.0 * producing.Sum(l => (long)l.BagsInTolerance) / totalBags : 0;

        // Temps utile / temps requis, summed over every bucket (stopped ones
        // too) before dividing: a cumul weighs each line by the time it was
        // required, never an average of the lines' TRS.
        var ratedRequired = lots.Sum(l => l.RatedRequiredMinutes);
        double? trsPercent = performancePercent.HasValue && ratedRequired > 0
            ? 100.0 * usefulMinutes / ratedRequired
            : null;
        var maxSpeed = producing.Max(l => l.MaxSpeedBagsPerMinute);

        return new KpiSummary
        {
            TotalBags = totalBags,
            BagsUnderTolerance = producing.Sum(l => (long)l.BagsUnderTolerance),
            BagsInTolerance = producing.Sum(l => (long)l.BagsInTolerance),
            BagsOverTolerance = producing.Sum(l => (long)l.BagsOverTolerance),
            BagsIndeterminate = producing.Sum(l => (long)l.BagsIndeterminate),
            MetalRejects = producing.Sum(l => (long)l.MetalRejects),
            SpeedBagsPerMinute = Math.Round(actualRate, 1),
            MaxSpeedBagsPerMinute = maxSpeed > 0 ? Math.Round(maxSpeed, 1) : null,
            AvailabilityPercent = Math.Round(availabilityPercent, 1),
            PerformancePercent = performancePercent is { } p ? Math.Round(p, 1) : null,
            QualityPercent = Math.Round(qualityPercent, 1),
            TrsPercent = trsPercent is { } t ? Math.Round(t, 1) : null,
            WeightDeviationGrams = deviationGrams,
            WeightDeviationPercent = deviationPercent,
        };
    }

    // Bags-weighted average weight over bags whose weight data is trustworthy;
    // null when there's none (e.g. CC01's corrupt running average).
    public static double? WeightedAverageWeight(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        var bags = list.Sum(l => l.WeighedBags);
        return bags > 0 ? list.Sum(l => l.WeighedWeightSum) / bags : null;
    }

    // Average weight of one production's weighed bags at its current target
    // in these buckets (header line), null when it has none.
    public static double? ProductionAverageWeight(IEnumerable<ProductionBucket> lots, string productionId, double target)
    {
        var parts = lots.SelectMany(l => l.WeightParts).Where(p => p.ProductionId == productionId && p.Target == target).ToList();
        var bags = parts.Sum(p => p.Bags);
        return bags > 0 ? parts.Sum(p => p.WeightSum) / bags : null;
    }

    private static (double? Grams, double? Percent) WeightedDeviation(IEnumerable<ProductionBucket> lots)
    {
        var list = lots.ToList();
        var bags = list.Sum(l => l.WeighedBags);
        var targetSum = list.Sum(l => l.WeighedTargetSum);
        if (bags <= 0 || targetSum <= 0)
            return (null, null);
        var deviationSum = list.Sum(l => l.WeighedDeviationSum);
        return (Math.Round(deviationSum / bags, 2), Math.Round(100.0 * deviationSum / targetSum, 2));
    }

    // Colour band of the weight deviation, relative to the target, against
    // the machine's limits (Parametres > Machines, see WeightBand).
    public static KpiSeverity WeightDeviationSeverity(double? deviationPercent, WeightBand band)
        => band.Severity(deviationPercent);

    // Share (0-100) of the required time (observed, minus the planned stops
    // the line was stopped in) in which the line produced bags. Over the
    // productions with a known nominal rate when there are any, i.e. the
    // time the TRS covers: a line with no rate set would otherwise pull the
    // D of a cumul down without counting in its P, Q or TRS.
    public static double AvailabilityPercent(IReadOnlyCollection<ProductionBucket> lots)
    {
        var rated = lots.Where(l => l.IsRated).ToList();
        var basis = rated.Sum(l => l.RequiredMinutes) > 0 ? rated : lots.ToList();
        var required = basis.Sum(l => l.RequiredMinutes);
        return required > 0 ? Math.Min(100, 100.0 * basis.Sum(l => l.RunMinutes) / required) : 0;
    }

    // Bags per team (TeamScheduleService roster) over the period. Every
    // configured team is listed, even at 0, so the bars compare like for like;
    // hours on a poste with no team assigned go to "Sans equipe".
    public static List<TeamKpi> ByTeam(IReadOnlyCollection<ProductionBucket> lots, TeamScheduleService teamSchedule)
    {
        var byTeam = lots
            .GroupBy(l => teamSchedule.GetTeamForShift(TeamScheduleService.GetShiftDate(l.StartTime, l.ShiftId), l.ShiftId))
            .ToDictionary(g => g.Key, g => g.Sum(l => (long)l.TotalBags));

        var teamNames = teamSchedule.TeamNames;
        var result = teamNames
            .Select(name => new TeamKpi { TeamName = name, TotalBags = byTeam.GetValueOrDefault(name) })
            .ToList();
        var unassigned = byTeam.Where(kv => !teamNames.Contains(kv.Key)).Sum(kv => kv.Value);
        if (unassigned > 0)
            result.Add(new TeamKpi { TeamName = "Sans équipe", TotalBags = unassigned });
        return result;
    }

    public static List<DailyProduction> ByDay(IReadOnlyCollection<ProductionBucket> lots)
    {
        return lots
            .GroupBy(l => l.StartTime.Date)
            .Select(g => new DailyProduction { Date = g.Key, TotalBags = g.Sum(l => (long)l.TotalBags) })
            .OrderBy(d => d.Date)
            .ToList();
    }

    // Grouped by shift date, not calendar date: a Nuit poste (22h-06h) is one
    // bar on the day it started, not split across two days.
    public static List<DailyProduction> ByDayForShift(IReadOnlyCollection<ProductionBucket> lots, int shiftId)
    {
        return lots
            .Where(l => l.ShiftId == shiftId)
            .GroupBy(l => TeamScheduleService.GetShiftDate(l.StartTime, shiftId))
            .Select(g => new DailyProduction { Date = g.Key, TotalBags = g.Sum(l => (long)l.TotalBags) })
            .OrderBy(d => d.Date)
            .ToList();
    }
}
