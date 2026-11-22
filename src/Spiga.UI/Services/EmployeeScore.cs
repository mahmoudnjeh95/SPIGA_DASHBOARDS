using Spiga.Core.Staff;

namespace Spiga.UI.Services;

// Ready: can be given the post. Soon: has held it, to be confirmed with a
// few more postes. NotYet: tried, not convincing yet. Untested: no higher
// post held in the period. NoHigherPost: nothing above his function.
public enum Readiness
{
    Ready,
    Soon,
    NotYet,
    Untested,
    NoHigherPost,
}

// One rule of the verdict, shown with a tick or a cross.
public sealed record ReadinessCheck(string Text, bool Ok);

// A score from 0 to 100 for one of the four groups (null: no data, e.g.
// performance for someone who holds no machine), and the reason in words.
public sealed record GroupScore(string Name, double? Score, string Summary);

// The decision view of a profile: four group scores, an overall score, the
// verdict for the next post up, and strengths / points to watch in words.
// Every rule is written here, in plain numbers, so a chef can check it.
public sealed class EmployeeScore
{
    public GroupScore Polyvalence { get; init; } = null!;
    public GroupScore Performance { get; init; } = null!;
    public GroupScore StopsQuality { get; init; } = null!;
    public GroupScore Presence { get; init; } = null!;
    public double? Overall { get; init; }
    public StaffFunction? Target { get; init; }
    public Readiness Readiness { get; init; }
    public IReadOnlyList<ReadinessCheck> Checks { get; init; } = Array.Empty<ReadinessCheck>();
    public IReadOnlyList<string> Strengths { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Watch { get; init; } = Array.Empty<string>();

    public IEnumerable<GroupScore> Groups => new[] { Polyvalence, Performance, StopsQuality, Presence };

    // Overall: performance 35 %, stops and quality 25 %, presence 25 %,
    // polyvalence 15 %, over the groups that have a score.
    private static readonly (Func<EmployeeScore, GroupScore> Group, double Weight)[] Weights =
    {
        (s => s.Performance, 35), (s => s.StopsQuality, 25), (s => s.Presence, 25), (s => s.Polyvalence, 15),
    };

    public const double ReadyOverall = 75;
    public const double SoonOverall = 60;
    public const double ReadyPresence = 70;
    public const double ReadyGap = -2;

    public static string Band(double? score) => score is not { } s ? "nd" : s >= 75 ? "ok" : s >= 50 ? "warn" : "bad";

    public static string ReadinessLabel(Readiness r) => r switch
    {
        Readiness.Ready => "Prêt",
        Readiness.Soon => "Bientôt prêt",
        Readiness.NotYet => "Pas encore",
        Readiness.Untested => "Pas encore testé",
        _ => "—",
    };

    public static string ReadinessCss(Readiness r) => r switch
    {
        Readiness.Ready => "ok",
        Readiness.Soon => "warn",
        Readiness.NotYet => "bad",
        _ => "nd",
    };

    private static double Clamp(double v) => Math.Clamp(v, 0, 100);

    // The next function up from his own (manutention -> conducteur ->
    // chef d'équipe); none for the top ones and those outside the ladder.
    public static StaffFunction? NextUp(StaffFunction f) => f switch
    {
        StaffFunction.Emballage => StaffFunction.Conducteur,
        StaffFunction.MiseEnCarton => StaffFunction.Conducteur,
        StaffFunction.Nettoyage => StaffFunction.Emballage,
        StaffFunction.Conducteur => StaffFunction.ChefLigne,
        StaffFunction.ChefLigne => StaffFunction.ChefConditionnement,
        _ => null,
    };

    public static EmployeeScore Of(EmployeeProfile p)
    {
        var strengths = new List<string>();
        var watch = new List<string>();

        // 1. Polyvalence: 30 points per post held as autonome, 20 formé,
        // 10 occasionnel, + 10 per higher post held; 100 at most.
        var poly = Clamp(p.Skills.Sum(s => s.Level switch { SkillLevel.Autonome => 30, SkillLevel.Forme => 20, _ => 10 })
                         + 10 * p.Skills.Count(s => StaffFunctions.Rank(s.Function) > StaffFunctions.Rank(p.Function)));
        var trained = p.Skills.Count(s => s.Level != SkillLevel.Occasionnel);
        if (p.Skills.Count >= 3)
            strengths.Add($"Polyvalent : {p.Skills.Count} postes tenus, dont {trained} formé(s) ou autonome(s).");
        if (p.StepUps > 0)
            strengths.Add($"A tenu {p.StepUps} fois un poste supérieur ({p.StepUpHours:0.#} h).");
        else if (p.Skills.Count <= 1)
            watch.Add("Un seul poste tenu sur la période : polyvalence à développer.");

        // 2. Performance: 80 at the machines' usual TRS, + 4 per point
        // above, - 4 per point below (100 from +5 pts, 0 at -20 pts).
        var gap = p.Own.Trs - p.Usual.Trs;
        double? perf = gap is { } g ? Clamp(80 + 4 * g) : null;
        if (gap is { } gp && gp >= 2)
            strengths.Add($"TRS {gp:+0.#;-0.#} pts au-dessus de l'habituel des mêmes machines.");
        else if (gap is { } gm && gm <= -5)
            watch.Add($"TRS {gm:+0.#;-0.#} pts sous l'habituel des mêmes machines.");
        if (p.TrsSpread is > 10)
            watch.Add($"Résultats irréguliers d'un poste à l'autre (± {p.TrsSpread:0.#} pts).");

        // 3. Stops and quality: mean of three parts, each 100 at the usual
        // level or better: stop minutes (- 50 per 100 % more than usual),
        // bags in tolerance (- 20 per point below), packaging waste (as stops).
        var parts = new List<double>();
        if (p.Own.StopPerHour is { } st && p.Usual.StopPerHour is { } ust)
        {
            var ratio = ust > 0.1 ? st / ust : st <= 0.1 ? 1 : 2;
            parts.Add(Clamp(100 - 50 * Math.Max(0, ratio - 1)));
            if (ratio <= 0.8)
                strengths.Add($"Moins d'arrêts que d'habitude ({st:0.#} min/h contre {ust:0.#}).");
            else if (ratio >= 1.5)
                watch.Add($"Plus d'arrêts que d'habitude ({st:0.#} min/h contre {ust:0.#}).");
        }
        if (p.Own.Quality is { } q && p.Usual.Quality is { } uq)
        {
            parts.Add(Clamp(100 + 20 * Math.Min(0, q - uq)));
            if (q - uq <= -1)
                watch.Add($"Moins de sacs dans la tolérance ({q:0.#} % contre {uq:0.#} %).");
        }
        if (p.WastePerTonne is { } w && p.UsualWastePerTonne is { } uw)
        {
            var ratio = uw > 0.01 ? w / uw : w <= 0.01 ? 1 : 2;
            parts.Add(Clamp(100 - 50 * Math.Max(0, ratio - 1)));
            if (ratio >= 1.5)
                watch.Add($"Plus de déchets d'emballage que d'habitude ({w:0.#} kg/t contre {uw:0.#}).");
        }
        double? stops = parts.Count == 0 ? null : parts.Average();

        // 4. Presence: 100 at full attendance, - 5 per point missing, - 5 per
        // lateness or early departure (30 at most), - 10 from a Bradford of
        // 50, - 20 from 200.
        var a = p.Presence;
        double? presence = a.Rate is { } rate
            ? Clamp(100 - 5 * (100 - rate) - Math.Min(30, 5 * (a.Lates + a.Early)) - (a.Bradford >= 200 ? 20 : a.Bradford >= 50 ? 10 : 0))
            : null;
        if (a.Rate is >= 99 && a.Lates == 0 && a.Present >= 3)
            strengths.Add($"Assidu : présent à tous ses postes ({a.Present}), aucun retard.");
        if (a.A > 0)
            watch.Add($"{a.A} absence(s) non justifiée(s) (A).");
        if (a.Lates + a.Early >= 3)
            watch.Add($"{a.Lates} retard(s) et {a.Early} départ(s) anticipé(s).");
        if (a.Bradford >= 50)
            watch.Add($"Facteur de Bradford {a.Bradford} : absences courtes et répétées.");

        var score = new EmployeeScore
        {
            Polyvalence = new("Polyvalence", poly, $"{p.Skills.Count} poste(s) · {p.StepUps} supérieur(s)"),
            Performance = new("Performance", perf, gap is { } gg ? $"TRS {gg:+0.#;-0.#;0} pts vs habituel" : "pas de machine tenue"),
            StopsQuality = new("Arrêts et qualité", stops, stops == null ? "pas de machine tenue" : "arrêts, tolérance, déchets vs habituel"),
            Presence = new("Présence", presence, a.Rate is { } r2 ? $"{r2:0.#} % présent · {a.Lates} retard(s)" : "aucun poste dû"),
            Strengths = strengths,
            Watch = watch,
        };
        var known = Weights.Where(x => x.Group(score).Score != null).ToList();
        double? overall = known.Count == 0 ? null : known.Sum(x => x.Group(score).Score!.Value * x.Weight) / known.Sum(x => x.Weight);

        // The verdict for the next post up.
        var target = NextUp(p.Function);
        var onTarget = target is { } t ? p.Skills.Where(s => s.Function == t).ToList() : new List<Skill>();
        var postes = onTarget.Sum(s => s.Postes);
        var lots = onTarget.Where(s => s.Gap != null).ToList();
        double? targetGap = lots.Count == 0 ? null : lots.Sum(s => s.Gap!.Value * s.Postes) / lots.Sum(s => s.Postes);
        var checks = new List<ReadinessCheck>();
        Readiness readiness;
        if (target == null || postes == 0)
        {
            readiness = target == null ? Readiness.NoHigherPost : Readiness.Untested;
            if (target != null)
                checks.Add(new($"N'a pas encore tenu de poste de {StaffFunctions.Label(target.Value).ToLowerInvariant()} sur la période", false));
        }
        else
        {
            var held = new ReadinessCheck($"A tenu le poste {postes} fois (au moins {EmployeeProfiles.FormedPostes})", postes >= EmployeeProfiles.FormedPostes);
            var level = new ReadinessCheck(targetGap is { } tg ? $"TRS sur ce poste {tg:+0.#;-0.#;0} pts vs habituel (au moins {ReadyGap:0})" : "TRS sur ce poste inconnu",
                targetGap is { } tg2 && tg2 >= ReadyGap || targetGap == null && target is StaffFunction.ChefLigne or StaffFunction.ChefConditionnement);
            var pres = new ReadinessCheck($"Présence {presence:0}/100 (au moins {ReadyPresence:0})", presence >= ReadyPresence);
            var all = new ReadinessCheck($"Note globale {overall:0}/100 (au moins {ReadyOverall:0})", overall >= ReadyOverall);
            checks.AddRange(new[] { held, level, pres, all });
            readiness = checks.All(c => c.Ok) ? Readiness.Ready
                : overall >= SoonOverall && pres.Ok ? Readiness.Soon
                : Readiness.NotYet;
        }

        return new EmployeeScore
        {
            Polyvalence = score.Polyvalence,
            Performance = score.Performance,
            StopsQuality = score.StopsQuality,
            Presence = score.Presence,
            Strengths = strengths,
            Watch = watch,
            Overall = overall,
            Target = target,
            Readiness = readiness,
            Checks = checks,
        };
    }
}
