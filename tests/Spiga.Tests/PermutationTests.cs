using Spiga.Core.Security;
using Spiga.Core.Staff;
using SpigaDashboards.App.ViewModels;

namespace Spiga.Tests;

// The test plant: Matin = Equipe B, Après-midi and Nuit = Equipe A.
public class PermutationTests
{
    private static readonly DateTime Today = new(2026, 11, 17);

    private sealed record Setup(TestApp App, Actor Resp, Actor ChefB, Actor ChefA, Dictionary<string, Employee> People)
    {
        public Employee this[string name] => People[name];
    }

    private static async Task<Setup> TeamsAsync(TestApp app)
    {
        app.Clock.Now = Today.AddHours(13).AddMinutes(30);
        var admin = await app.SetupAdminAsync();
        var resp = await app.UserAsync(admin, "resp.prod", "resp-production");
        var chefB = await app.UserAsync(admin, "chef.b", "chef-equipe", TestApp.Team("Equipe B"));
        var chefA = await app.UserAsync(admin, "chef.a", "chef-equipe", TestApp.Team("Equipe A"));
        var from = Today.AddDays(-30);
        var people = new Dictionary<string, Employee>();
        async Task Add(string name, string team, StaffFunction f, string? machine = null)
            => people[name] = await app.Staff.AddEmployeeAsync(resp, new EmployeeDraft(name, ""), new PostingDraft(from, team, f, machine, null));
        await Add("TRIKI Faez", "Equipe B", StaffFunction.Conducteur, "PC01");
        await Add("CHOURA Wassim", "Equipe B", StaffFunction.Nettoyage);
        await Add("AYADI Khalil", "Equipe A", StaffFunction.Conducteur, "PL01");
        await Add("MEJRI Sami", "Equipe A", StaffFunction.Nettoyage);
        return new Setup(app, resp, chefB, chefA, people);
    }

    private static AttendanceLine Line(AttendanceSheet s, Employee e) => s.Lines.Single(l => l.EmployeeId == e.Id);

    private static void ConfirmAll(AttendanceSheet s)
    {
        foreach (var l in s.Lines.Where(l => l.Status == LineStatus.AConfirmer))
            l.Status = LineStatus.Present;
    }

    [Fact]
    public async Task Two_people_swap_their_postes_and_each_sheet_shows_it()
    {
        using var app = new TestApp();
        var t = await TeamsAsync(app);
        var matin = await app.Attendance.OpenAsync(t.ChefB, Today, 1);

        var (refusal, candidates) = await app.Attendance.PermutationCandidatesAsync(t.ChefB, matin, t["TRIKI Faez"].Id);
        Assert.Null(refusal);
        var ayadi = candidates.Single(c => c.Employee.Id == t["AYADI Khalil"].Id);
        Assert.Null(ayadi.Refusal);
        Assert.Equal(2, ayadi.ShiftId);
        // Same function required by default: the cleaner of Equipe A is greyed.
        Assert.NotNull(candidates.Single(c => c.Employee.Id == t["MEJRI Sami"].Id).Refusal);

        var p = await app.Attendance.PermuteAsync(t.ChefB, matin, t["TRIKI Faez"].Id, t["AYADI Khalil"].Id);
        Assert.Equal(2, p.ShiftB);

        matin = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        var triki = Line(matin, t["TRIKI Faez"]);
        Assert.Equal(LineStatus.Ailleurs, triki.Status);
        Assert.Equal("AYADI Khalil", triki.PermutedWith);
        var came = Line(matin, t["AYADI Khalil"]);
        Assert.True(came.IsRenfort);
        Assert.Equal(LineStatus.AConfirmer, came.Status);
        Assert.Equal(Today.AddHours(6), came.EntryAt);
        Assert.Equal("PC01", came.MachineId);

        ConfirmAll(matin);
        var saved = await app.Attendance.SaveAsync(t.ChefB, matin, validate: true, null);
        Assert.Equal(SheetStatus.Validee, saved.Status);
        Assert.Equal(LineStatus.Ailleurs, Line(saved, t["TRIKI Faez"]).Status);

        // The afternoon: Equipe A's sheet has Triki as renfort, Ayadi permuted.
        app.Clock.Now = Today.AddHours(21).AddMinutes(30);
        var aprem = await app.Attendance.OpenAsync(t.ChefA, Today, 2);
        Assert.Equal(LineStatus.Ailleurs, Line(aprem, t["AYADI Khalil"]).Status);
        var trikiThere = Line(aprem, t["TRIKI Faez"]);
        Assert.True(trikiThere.IsRenfort);
        Assert.Equal(Today.AddHours(14), trikiThere.EntryAt);
        ConfirmAll(aprem);
        var done = await app.Attendance.SaveAsync(t.ChefA, aprem, validate: true, null);
        Assert.Equal(SheetStatus.Validee, done.Status);

        // A permutation is no overtime: one poste each that day.
        var book = await app.Overtime.BookAsync(Today);
        Assert.Equal(0, book.MonthMinutes(t["TRIKI Faez"].Id));
        Assert.Equal(0, book.MonthMinutes(t["AYADI Khalil"].Id));
    }

    [Fact]
    public async Task The_same_function_rule_is_a_setting()
    {
        using var app = new TestApp();
        var t = await TeamsAsync(app);
        var matin = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        await Assert.ThrowsAsync<RuleViolationException>(
            () => app.Attendance.PermuteAsync(t.ChefB, matin, t["TRIKI Faez"].Id, t["MEJRI Sami"].Id));

        // A chef d'équipe may not change it; the production manager may.
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Rules.SetPermutationAsync(t.ChefB, new PermutationRule { SameFunction = false }));
        await app.Rules.SetPermutationAsync(t.Resp, new PermutationRule { SameFunction = false });

        var (_, candidates) = await app.Attendance.PermutationCandidatesAsync(t.ChefB, matin, t["TRIKI Faez"].Id);
        Assert.Null(candidates.Single(c => c.Employee.Id == t["MEJRI Sami"].Id).Refusal);
        await app.Attendance.PermuteAsync(t.ChefB, matin, t["TRIKI Faez"].Id, t["MEJRI Sami"].Id);
    }

    [Fact]
    public async Task Cancelling_puts_the_saved_draft_back()
    {
        using var app = new TestApp();
        var t = await TeamsAsync(app);
        var draft = await app.Attendance.SaveAsync(t.ChefB, await app.Attendance.OpenAsync(t.ChefB, Today, 1), validate: false, null);
        var p = await app.Attendance.PermuteAsync(t.ChefB, draft, t["TRIKI Faez"].Id, t["AYADI Khalil"].Id);

        // The saved draft followed at once, with a revision.
        var stored = (await app.AttendanceStore.FindAsync(Today, 1, "Equipe B"))!;
        Assert.Equal(LineStatus.Ailleurs, Line(stored, t["TRIKI Faez"]).Status);
        Assert.Contains(stored.Lines, l => l.EmployeeId == t["AYADI Khalil"].Id && l.IsRenfort);
        Assert.Contains(await app.AttendanceStore.ListRevisionsAsync(stored.Id), r => r.Action == "Pointage : permutation");

        // Pointing Triki here while permuted is refused.
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Line(sheet, t["TRIKI Faez"]).Status = LineStatus.Present;
        Line(sheet, t["TRIKI Faez"]).EntryAt = sheet.Start;
        Line(sheet, t["TRIKI Faez"]).ExitAt = sheet.End;
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, null));

        await app.Attendance.CancelPermutationAsync(t.ChefB, p.Id);
        var back = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Assert.Equal(LineStatus.AConfirmer, Line(back, t["TRIKI Faez"]).Status);
        Assert.DoesNotContain(back.Lines, l => l.EmployeeId == t["AYADI Khalil"].Id);
        Assert.Null(Line(back, t["TRIKI Faez"]).PermutedWith);
    }

    [Fact]
    public async Task A_validated_sheet_keeps_its_permutation_and_refuses_new_ones()
    {
        using var app = new TestApp();
        var t = await TeamsAsync(app);
        var matin = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        var p = await app.Attendance.PermuteAsync(t.ChefB, matin, t["TRIKI Faez"].Id, t["AYADI Khalil"].Id);
        matin = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(matin);
        await app.Attendance.SaveAsync(t.ChefB, matin, validate: true, null);

        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.CancelPermutationAsync(t.ChefB, p.Id));
        var validated = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Assert.Equal("AYADI Khalil", Line(validated, t["TRIKI Faez"]).PermutedWith);
        var (refusal, _) = await app.Attendance.PermutationCandidatesAsync(t.ChefB, validated, t["CHOURA Wassim"].Id);
        Assert.NotNull(refusal);
    }

    // ---------- personnel rules ----------

    private static AttendanceSheet Sheet(DateTime date, int shift, params AttendanceLine[] lines)
        => new() { ProductionDate = date, ShiftId = shift, Team = "Equipe B", Status = SheetStatus.Validee, Lines = lines.ToList() };

    private static AttendanceLine Present(DateTime date, int lateMinutes = 0, int earlyMinutes = 0) => new()
    {
        EmployeeId = 7, FullName = "TRIKI Faez", HomeTeam = "Equipe B", Status = LineStatus.Present,
        EntryAt = date.AddHours(6).AddMinutes(lateMinutes), ExitAt = date.AddHours(14).AddMinutes(-earlyMinutes),
    };

    private static AttendanceLine Absent(AbsenceCode code)
        => new() { EmployeeId = 7, FullName = "TRIKI Faez", HomeTeam = "Equipe B", Status = LineStatus.Absent, Absence = code };

    [Fact]
    public void Successive_lateness_is_counted_over_the_last_postes_past_the_tolerance()
    {
        var rules = new PersonnelRules();
        rules.Lateness.Count = 3;
        rules.Lateness.Window = 3;
        rules.Lateness.ToleranceMinutes = 5;
        var sheets = new[]
        {
            Sheet(Today.AddDays(-3), 1, Present(Today.AddDays(-3), lateMinutes: 20)),
            Sheet(Today.AddDays(-2), 1, Present(Today.AddDays(-2), lateMinutes: 10)),
            Sheet(Today.AddDays(-1), 1, Present(Today.AddDays(-1), lateMinutes: 15)),
            Sheet(Today, 1, Present(Today, lateMinutes: 30)),
        };
        var alert = Assert.Single(PersonnelAnalysis.Alerts(sheets, rules, Today), a => a.Kind == PersonAlertKind.Retards);
        Assert.Equal(3, alert.Count);
        Assert.Equal(10 + 15 + 30, alert.Minutes);
        Assert.Equal(Today, alert.Last);

        // Under the tolerance it does not count: not 3 in a row any more.
        var onTime = sheets.Take(3).Append(Sheet(Today, 1, Present(Today, lateMinutes: 5))).ToArray();
        Assert.DoesNotContain(PersonnelAnalysis.Alerts(onTime, rules, Today), a => a.Kind == PersonAlertKind.Retards);
    }

    [Fact]
    public void Absences_count_only_the_chosen_codes_and_early_departures_their_minutes()
    {
        var rules = new PersonnelRules();
        rules.Absence.Count = 2;
        rules.Absence.Window = 30;
        rules.Absence.Unit = WindowUnit.Jours;
        rules.EarlyLeave.Count = 2;
        var sheets = new[]
        {
            Sheet(Today.AddDays(-10), 1, Absent(AbsenceCode.A)),
            Sheet(Today.AddDays(-5), 1, Absent(AbsenceCode.M)),
            Sheet(Today.AddDays(-2), 1, Present(Today.AddDays(-2), earlyMinutes: 60)),
            Sheet(Today.AddDays(-1), 1, Present(Today.AddDays(-1), earlyMinutes: 30)),
        };
        var alerts = PersonnelAnalysis.Alerts(sheets, rules, Today);
        Assert.DoesNotContain(alerts, a => a.Kind == PersonAlertKind.Absences);
        var early = Assert.Single(alerts, a => a.Kind == PersonAlertKind.DepartsAnticipes);
        Assert.Equal(90, early.Minutes);

        rules.AbsenceCodes.Add(AbsenceCode.M);
        var absences = Assert.Single(PersonnelAnalysis.Alerts(sheets, rules, Today), a => a.Kind == PersonAlertKind.Absences);
        Assert.Equal(2, absences.Count);
        Assert.Equal(AlertLevel.Critique, absences.Level);

        // Switched off: nothing.
        rules.Absence.Enabled = false;
        Assert.DoesNotContain(PersonnelAnalysis.Alerts(sheets, rules, Today), a => a.Kind == PersonAlertKind.Absences);
    }

    [Fact]
    public async Task Attention_rules_are_validated_saved_and_audited()
    {
        using var app = new TestApp();
        var t = await TeamsAsync(app);
        var rules = await app.Rules.AttentionAsync();
        Assert.True(rules.Machines.LowTrs);
        Assert.Null(rules.Machines.TrsPointsBelowTarget);

        rules.Machines.TrsPointsBelowTarget = 8;
        rules.Personnel.Lateness.Count = 9;
        rules.Personnel.Lateness.Window = 5;
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Rules.SetAttentionAsync(t.Resp, rules));

        rules.Personnel.Lateness.Count = 4;
        await app.Rules.SetAttentionAsync(t.Resp, rules);
        var again = await app.Rules.AttentionAsync();
        Assert.Equal(8, again.Machines.TrsPointsBelowTarget);
        Assert.Equal(4, again.Personnel.Lateness.Count);
        Assert.Equal(WindowUnit.Postes, again.Personnel.Lateness.Unit);
        var log = await app.Audit.QueryAsync(new Spiga.Core.Audit.AuditQuery(Today.AddDays(-1), Today.AddDays(1)));
        Assert.Contains(log, e => e.Action == Spiga.Core.Audit.AuditActions.AttentionRulesChanged);
    }

    // ---------- production days ----------

    [Fact]
    public void Dashboard_periods_run_from_6_to_6()
    {
        var night = new DateTime(2026, 11, 17, 3, 0, 0);
        Assert.Equal((new DateTime(2026, 11, 15, 6, 0, 0), new DateTime(2026, 11, 16, 6, 0, 0)), PeriodRanges.Get(DashboardPeriod.Yesterday, night));
        Assert.Equal((new DateTime(2026, 11, 16, 6, 0, 0), new DateTime(2026, 11, 17, 6, 0, 0)), PeriodRanges.Get(DashboardPeriod.Day, night));
        var morning = new DateTime(2026, 11, 17, 10, 0, 0);
        Assert.Equal((new DateTime(2026, 11, 17, 6, 0, 0), new DateTime(2026, 11, 18, 6, 0, 0)), PeriodRanges.Get(DashboardPeriod.Day, morning));
        Assert.Equal((new DateTime(2026, 11, 1, 6, 0, 0), new DateTime(2026, 11, 18, 6, 0, 0)), PeriodRanges.Get(DashboardPeriod.Month, morning));
        Assert.Equal((new DateTime(2026, 11, 10, 6, 0, 0), new DateTime(2026, 11, 13, 6, 0, 0)),
            PeriodRanges.Get(DashboardPeriod.Custom, morning, new DateTime(2026, 11, 10), new DateTime(2026, 11, 12)));
    }
}
