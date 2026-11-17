using SpigaDashboards.App.Models;

namespace SpigaDashboards.App.Services;

// The daily planned breaks (AppSettings.PlannedBreaks) as time windows.
public static class BreakSchedule
{
    public const int SecondsPerDay = 86400;

    // Break windows in seconds of the day [Start, End), sorted, overlapping
    // ones merged. End can pass midnight (86400) for a break across it.
    public static List<(int Start, int End)> DayWindows(IEnumerable<PlannedBreak> breaks)
    {
        var merged = new List<(int Start, int End)>();
        foreach (var b in breaks.Where(b => b.DurationMinutes > 0).OrderBy(b => b.Start))
        {
            var start = (int)b.Start.TotalSeconds % SecondsPerDay;
            var end = start + b.DurationMinutes * 60;
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
        }
        return merged;
    }

    // Minutes of [from, to) that fall inside a planned break.
    public static double OverlapMinutes(IReadOnlyList<(int Start, int End)> windows, DateTime from, DateTime to)
    {
        if (to <= from || windows.Count == 0)
            return 0;
        var seconds = 0.0;
        // From the day before: a break across midnight reaches into from's day.
        for (var day = from.Date.AddDays(-1); day < to; day = day.AddDays(1))
        {
            foreach (var (start, end) in windows)
            {
                var s = day.AddSeconds(start);
                var e = day.AddSeconds(end);
                var overlap = (Min(to, e) - Max(from, s)).TotalSeconds;
                if (overlap > 0)
                    seconds += overlap;
            }
        }
        return seconds / 60;
    }

    // The parts of [from, to) outside every planned break, so a maintenance
    // slot over a break isn't deducted twice.
    public static List<(DateTime Start, DateTime End)> Outside(IReadOnlyList<(int Start, int End)> windows, DateTime from, DateTime to)
    {
        var pieces = new List<(DateTime Start, DateTime End)>();
        var cursor = from;
        var breaks = new List<(DateTime Start, DateTime End)>();
        for (var day = from.Date.AddDays(-1); day < to; day = day.AddDays(1))
            breaks.AddRange(windows.Select(w => (day.AddSeconds(w.Start), day.AddSeconds(w.End))));
        foreach (var (s, e) in breaks.Where(b => b.End > from && b.Start < to).OrderBy(b => b.Start))
        {
            if (s > cursor)
                pieces.Add((cursor, s));
            cursor = Max(cursor, e);
        }
        if (to > cursor)
            pieces.Add((cursor, to));
        return pieces;
    }

    // "10:00-10:20, 13:40-14:00" for headers.
    public static string Describe(IEnumerable<PlannedBreak> breaks)
    {
        var text = string.Join(", ", DayWindows(breaks).Select(w => $"{Clock(w.Start)}-{Clock(w.End)}"));
        return text.Length == 0 ? "aucune" : text;
    }

    private static string Clock(int seconds) => TimeSpan.FromSeconds(seconds % SecondsPerDay).ToString(@"hh\:mm");

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
}
