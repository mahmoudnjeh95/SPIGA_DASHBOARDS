using Spiga.Core.Audit;
using Spiga.Core.Security;
using Spiga.Core.Staff;

namespace Spiga.Tests;

// The test plant: Matin = Equipe B, Après-midi and Nuit = Equipe A; now =
// 17/11/2026 10:00 (Matin of the 17th in progress).
public class PointageTests
{
    private static readonly DateTime Today = new(2026, 11, 17);

    private sealed class Setup
    {
        public required TestApp App { get; init; }
        public required Actor Resp { get; init; }
        public required Actor ChefB { get; init; }
        public required Actor ChefA { get; init; }
        public required Dictionary<string, Employee> People { get; init; }
        public Employee this[string name] => People[name];
    }

    // Team B: a chef de conditionnement, a chef de ligne, two conducteurs, an
    // emballage worker, a cleaner. Team A: a conducteur.
    private static async Task<Setup> TeamAsync(TestApp app)
    {
        // 13:30: the morning poste can be validated (from an hour before its end).
        app.Clock.Now = Today.AddHours(13).AddMinutes(30);
        var admin = await app.SetupAdminAsync();
        var resp = await app.UserAsync(admin, "resp.prod", "resp-production");
        var chefB = await app.UserAsync(admin, "chef.b", "chef-equipe", TestApp.Team("Equipe B"));
        var chefA = await app.UserAsync(admin, "chef.a", "chef-equipe", TestApp.Team("Equipe A"));
        var line = await app.Staff.SaveLineAsync(resp, 0, "Pâte courte", new[] { "PC01", "PC02" }, true);
        var from = Today.AddDays(-30);
        var people = new Dictionary<string, Employee>();
        async Task Add(string name, string team, StaffFunction f, string? machine = null, int? lineId = null)
            => people[name] = await app.Staff.AddEmployeeAsync(resp, new EmployeeDraft(name, ""), new PostingDraft(from, team, f, machine, lineId));
        await Add("HAJ BRAHIM Makrem", "Equipe B", StaffFunction.ChefConditionnement);
        await Add("BEN HSEN Mondher", "Equipe B", StaffFunction.ChefLigne, lineId: line.Id);
        await Add("TRIKI Faez", "Equipe B", StaffFunction.Conducteur, "PC01");
        await Add("BABBOU Firas", "Equipe B", StaffFunction.Conducteur, "PC02");
        await Add("GUEDDICH Moez", "Equipe B", StaffFunction.Emballage, "CC01");
        await Add("CHOURA Wassim", "Equipe B", StaffFunction.Nettoyage);
        await Add("AYADI Khalil", "Equipe A", StaffFunction.Conducteur, "PL01");
        return new Setup { App = app, Resp = resp, ChefB = chefB, ChefA = chefA, People = people };
    }

    private static AttendanceLine Line(AttendanceSheet s, Setup t, string name) => s.Lines.Single(l => l.EmployeeId == t[name].Id);

    private static void ConfirmAll(AttendanceSheet s)
    {
        foreach (var l in s.Lines.Where(l => l.Status == LineStatus.AConfirmer))
            l.Status = LineStatus.Present;
    }

    [Fact]
    public async Task Sheet_opens_with_the_team_at_the_poste_hours_and_validates_once_every_row_is_confirmed()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);

        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Assert.True(sheet.IsNew);
        Assert.Equal("Equipe B", sheet.Team);
        Assert.Equal(6, sheet.Lines.Count);
        Assert.Equal("HAJ BRAHIM Makrem", sheet.Lines[0].FullName);
        Assert.All(sheet.Lines, l =>
        {
            Assert.Equal(LineStatus.AConfirmer, l.Status);
            Assert.Equal(Today.AddHours(6), l.EntryAt);
            Assert.Equal(Today.AddHours(14), l.ExitAt);
        });

        // Not everyone confirmed: no validation.
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null));

        // A draft can be kept with rows still to confirm.
        var draft = await app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, null);
        Assert.Equal(SheetStatus.Brouillon, draft.Status);

        sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(sheet);
        Line(sheet, t, "TRIKI Faez").EntryAt = Today.AddHours(6).AddMinutes(30);
        var absent = Line(sheet, t, "BABBOU Firas");
        absent.Status = LineStatus.Absent;
        absent.Absence = AbsenceCode.M;
        var saved = await app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null);

        Assert.Equal(SheetStatus.Validee, saved.Status);
        Assert.Equal("chef.b", saved.ValidatedBy);
        Assert.Null(Line(saved, t, "BABBOU Firas").EntryAt);
        var history = await app.Attendance.HistoryAsync(t.ChefB, saved);
        Assert.Equal(2, history.Count);
        Assert.Contains("TRIKI Faez : à confirmer → présent 06:30–14:00", history[^1].Changes);
        Assert.Contains("BABBOU Firas : à confirmer → M (Maladie)", history[^1].Changes);
        Assert.Contains(await app.AuditAsync(), e => e.Action == AuditActions.SheetValidated && e.Module == Modules.Pointage);
    }

    [Fact]
    public async Task Chef_sees_only_his_team_and_only_yesterday_and_today()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-1), 3));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-2), 1));
        await app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-1), 1);
        // Not started yet.
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.OpenAsync(t.ChefB, Today, 2));
        Assert.Equal(new[] { 1 }, app.Attendance.PostesOn(t.ChefB, Today).Select(p => p.ShiftId));
        Assert.Equal((Today, 1), app.Attendance.DefaultPoste(t.ChefB));
        Assert.Equal((Today.AddDays(-1), 3), app.Attendance.DefaultPoste(t.ChefA));

        // The production manager covers every team and the older days.
        var old = await app.Attendance.OpenAsync(t.Resp, Today.AddDays(-5), 3);
        Assert.Equal("Equipe A", old.Team);
    }

    [Fact]
    public async Task Invalid_hours_and_absences_without_code_are_refused()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(sheet);
        var triki = Line(sheet, t, "TRIKI Faez");

        triki.EntryAt = Today.AddHours(6).AddMinutes(7);
        Assert.Contains(AttendanceService.Problems(sheet, true), p => p.Message.Contains("pas de 5 minutes"));
        triki.EntryAt = Today.AddHours(3);
        Assert.Contains(AttendanceService.Problems(sheet, true), p => p.Message.Contains("hors du poste"));
        triki.EntryAt = Today.AddHours(14);
        Assert.Contains(AttendanceService.Problems(sheet, true), p => p.Message.Contains("après l'entrée"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, true, null));

        triki.EntryAt = Today.AddHours(6);
        triki.Status = LineStatus.Absent;
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, true, null));
        triki.Absence = AbsenceCode.A;

        // Every team member must be on the sheet.
        var cleaner = Line(sheet, t, "CHOURA Wassim");
        sheet.Lines.Remove(cleaner);
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, true, null));
        Assert.Contains("CHOURA Wassim", ex.Message);
        sheet.Lines.Add(cleaner);

        await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);
    }

    [Fact]
    public async Task Validated_sheet_is_corrected_with_a_reason_and_the_revision_keeps_what_changed()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(sheet);
        sheet = await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);

        Line(sheet, t, "GUEDDICH Moez").ExitAt = Today.AddHours(12);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, false, " "));
        var corrected = await app.Attendance.SaveAsync(t.ChefB, sheet, false, "Parti à midi, oublié");
        Assert.Equal(SheetStatus.Validee, corrected.Status);
        Assert.Equal(2, corrected.Version);

        var last = (await app.Attendance.HistoryAsync(t.ChefB, corrected))[^1];
        Assert.Equal(AuditActions.SheetCorrected, last.Action);
        Assert.Equal("Parti à midi, oublié", last.Note);
        Assert.Contains("GUEDDICH Moez : présent 06:00–14:00 → présent 06:00–12:00", last.Changes);

        // A stale screen cannot overwrite it.
        Line(sheet, t, "GUEDDICH Moez").ExitAt = Today.AddHours(13);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, false, "encore"));
    }

    [Fact]
    public async Task Correction_needs_the_right_and_stops_before_yesterday_without_the_old_postes_right()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-1), 1);
        ConfirmAll(sheet);
        sheet = await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);
        Assert.True(app.Attendance.CanCorrect(t.ChefB, sheet));

        // Two days later the chef no longer sees it; the manager still corrects it.
        app.Clock.Now = app.Clock.Now.AddDays(2);
        Assert.False(app.Attendance.CanCorrect(t.ChefB, sheet));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, false, "oubli"));
        Line(sheet, t, "TRIKI Faez").EntryAt = Today.AddDays(-1).AddHours(6).AddMinutes(15);
        await app.Attendance.SaveAsync(t.Resp, sheet, false, "Badge relu");
    }

    [Fact]
    public async Task Renfort_cannot_be_pointed_twice_at_the_same_time_and_the_home_chef_sees_where_he_worked()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);

        // AYADI (team A) worked the night of the 16th until 08:00 (overtime).
        var night = await app.Attendance.OpenAsync(t.ChefA, Today.AddDays(-1), 3);
        ConfirmAll(night);
        Line(night, t, "AYADI Khalil").ExitAt = Today.AddHours(8);
        await app.Attendance.SaveAsync(t.ChefA, night, true, null);

        // Then he is lent to team B's morning.
        var morning = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(morning);
        Assert.Contains(await app.Attendance.RenfortCandidatesAsync(t.ChefB, morning), c => c.Employee.Id == t["AYADI Khalil"].Id);
        var renfort = await app.Attendance.RenfortLineAsync(morning, t["AYADI Khalil"].Id);
        Assert.True(renfort.IsRenfort);
        renfort.Status = LineStatus.Present;
        morning.Lines.Add(renfort);
        var clash = await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, morning, true, null));
        Assert.Contains("deux endroits", clash.Message);

        renfort.EntryAt = Today.AddHours(8);
        await app.Attendance.SaveAsync(t.ChefB, morning, true, null);

        var away = await app.Attendance.AwayAsync(t.ChefA, "Equipe A", Today.AddDays(-1), Today);
        var (sheet, line) = Assert.Single(away);
        Assert.Equal("Equipe B", sheet.Team);
        Assert.Equal(Today.AddHours(8), line.EntryAt);
        // Chef B does not see team A's people away.
        Assert.Empty(await app.Attendance.AwayAsync(t.ChefB, "Equipe A", Today.AddDays(-1), Today));
    }

    [Fact]
    public async Task Temporary_assignment_takes_the_post_of_the_absent_one()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(sheet);
        var triki = Line(sheet, t, "TRIKI Faez");
        triki.Status = LineStatus.Absent;
        triki.Absence = AbsenceCode.A;

        var uncovered = AttendanceService.Uncovered(sheet);
        Assert.Equal("PC01", Assert.Single(uncovered).Post);

        var moez = Line(sheet, t, "GUEDDICH Moez");
        moez.ReplacesEmployeeId = triki.EmployeeId;
        moez.ActingFrom = Today.AddHours(6).AddMinutes(20);
        // Moez's own machine is now free.
        Assert.Equal("CC01", Assert.Single(AttendanceService.Uncovered(sheet)).Post);

        var saved = await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);
        var acting = Line(saved, t, "GUEDDICH Moez");
        Assert.Equal(StaffFunction.Conducteur, acting.ActingFunction);
        Assert.Equal("PC01", acting.ActingMachineId);
        Assert.True(acting.IsStepUp);

        // One cannot replace someone who is at his post.
        var editing = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Line(editing, t, "CHOURA Wassim").ReplacesEmployeeId = t["BABBOU Firas"].Id;
        Line(editing, t, "CHOURA Wassim").ActingFrom = Today.AddHours(6);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, editing, false, "test"));
    }

    [Fact]
    public async Task Staff_rules_machine_for_conducteurs_one_line_per_machine_and_dated_transfers()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);

        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Staff.AddEmployeeAsync(t.Resp, new EmployeeDraft("NOUVEAU Sami", ""), new PostingDraft(Today, "Equipe B", StaffFunction.Conducteur, null, null)));
        await Assert.ThrowsAsync<RuleViolationException>(() =>
            app.Staff.AddEmployeeAsync(t.Resp, new EmployeeDraft("triki faez", ""), new PostingDraft(Today, "Equipe B", StaffFunction.Nettoyage, null, null)));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Staff.SaveLineAsync(t.Resp, 0, "Autre", new[] { "PC02" }, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            app.Staff.AddEmployeeAsync(t.ChefB, new EmployeeDraft("NOUVEAU Sami", ""), new PostingDraft(Today, "Equipe B", StaffFunction.Nettoyage, null, null)));

        // Moez moves to team A from tomorrow: today's sheet still has him in B.
        await app.Staff.SavePostingAsync(t.Resp, t["GUEDDICH Moez"].Id, new PostingDraft(Today.AddDays(1), "Equipe A", StaffFunction.Conducteur, "PL01", null));
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Assert.Contains(sheet.Lines, l => l.EmployeeId == t["GUEDDICH Moez"].Id);
        var moez = (await app.StaffStore.GetEmployeeAsync(t["GUEDDICH Moez"].Id))!;
        Assert.Equal("Equipe A", moez.PostingAt(Today.AddDays(1))!.Team);
        Assert.Equal("Equipe B", moez.PostingAt(Today)!.Team);
    }

    [Fact]
    public async Task Sheet_is_validated_only_at_the_end_of_the_poste()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        app.Clock.Now = Today.AddHours(10);
        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        ConfirmAll(sheet);
        var ex = await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, true, null));
        Assert.Contains("13:00", ex.Message);
        sheet = await app.Attendance.SaveAsync(t.ChefB, sheet, false, null);
        app.Clock.Now = Today.AddHours(13);
        await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);
    }

    [Fact]
    public async Task Staff_list_pasted_from_Excel_is_checked_row_by_row()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var text = "Nom et prénom\tMatricule\tÉquipe\tFonction\tMachine\n"
                   + "NOUVEAU Sami\t1024\tB\tConducteur\tPC02\n"
                   + "TRIKI Faez\t\tB\tNettoyage\t\n"
                   + "AUTRE Ali\t\tZ\tEmballage\tCC01\n"
                   + "CHEF Ligne\t\tEquipe A\tchef de ligne\tpâte courte\n";
        var rows = await app.Staff.ParseImportAsync(text, Today);
        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].Ok);
        Assert.Equal(StaffFunction.Conducteur, rows[0].Posting!.Function);
        Assert.Equal("existe déjà", rows[1].Error);
        Assert.Contains("équipe", rows[2].Error);
        Assert.True(rows[3].Ok);
        Assert.Equal(2, await app.Staff.ImportAsync(t.Resp, rows));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Staff.ImportAsync(t.ChefB, rows));
    }

    [Fact]
    public async Task Missing_postes_are_listed_until_validated()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        // Not in use yet (no sheet ever entered): no reminder for the days before.
        Assert.Empty(await app.Attendance.MissingAsync(t.ChefB, Today.AddDays(-1)));

        var sheet = await app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-1), 1);
        sheet = await app.Attendance.SaveAsync(t.ChefB, sheet, false, null);
        var missing = Assert.Single(await app.Attendance.MissingAsync(t.ChefB, Today.AddDays(-1)));
        Assert.Equal((Today.AddDays(-1), 1, SheetStatus.Brouillon), (missing.Date, missing.ShiftId, missing.Status));

        sheet = await app.Attendance.OpenAsync(t.ChefB, Today.AddDays(-1), 1);
        ConfirmAll(sheet);
        await app.Attendance.SaveAsync(t.ChefB, sheet, true, null);
        Assert.Empty(await app.Attendance.MissingAsync(t.ChefB, Today.AddDays(-1)));
    }
}

public class PresenceAnalysisTests
{
    private static readonly DateTime Day = new(2026, 11, 17);

    private sealed class FakeHistory : IMachineHistory
    {
        public List<(string Machine, DateTime From, DateTime To)> Stops { get; } = new();
        public List<(string Machine, DateTime From, DateTime To)> Producing { get; } = new();

        private static double Overlap(DateTime a, DateTime b, DateTime from, DateTime to)
            => Math.Max(0, ((b < to ? b : to) - (a > from ? a : from)).TotalMinutes);

        public double StoppedMinutes(string m, DateTime from, DateTime to) => Stops.Where(s => s.Machine == m).Sum(s => Overlap(s.From, s.To, from, to));
        public double ProducingMinutes(string m, DateTime from, DateTime to) => Producing.Where(s => s.Machine == m).Sum(s => Overlap(s.From, s.To, from, to));
        public double? BagsPerMinute(string m, DateTime from, DateTime to) => 20;
        public double? TargetGrams(string m, DateTime from, DateTime to) => 500;
    }

    private static AttendanceLine Person(int id, string name, StaffFunction f, string? machine, LineStatus status, int entryMin = 0, int exitMin = 480)
        => new()
        {
            EmployeeId = id, FullName = name, Function = f, HomeTeam = "Equipe B", MachineId = machine, Status = status,
            EntryAt = status == LineStatus.Present ? Day.AddHours(6).AddMinutes(entryMin) : null,
            ExitAt = status == LineStatus.Present ? Day.AddHours(6).AddMinutes(exitMin) : null,
            Absence = status == LineStatus.Absent ? AbsenceCode.A : null,
        };

    private static AttendanceSheet Sheet(params AttendanceLine[] lines)
        => new() { Id = 1, ProductionDate = Day, ShiftId = 1, Team = "Equipe B", Status = SheetStatus.Validee, Lines = lines.ToList() };

    private static readonly IReadOnlyDictionary<int, ProductionLine> NoLines = new Dictionary<int, ProductionLine>();

    [Fact]
    public void Lateness_counts_against_production_only_when_the_machine_stood_still()
    {
        var history = new FakeHistory();
        history.Stops.Add(("PC01", Day.AddHours(6), Day.AddHours(6).AddMinutes(20)));
        history.Producing.Add(("PC02", Day.AddHours(6), Day.AddHours(14)));
        var sheet = Sheet(
            Person(1, "TRIKI", StaffFunction.Conducteur, "PC01", LineStatus.Present, entryMin: 30),
            Person(2, "BABBOU", StaffFunction.Conducteur, "PC02", LineStatus.Present, entryMin: 30),
            Person(3, "CHOURA", StaffFunction.Nettoyage, null, LineStatus.Present, entryMin: 15));

        var events = PresenceAnalysis.Events(new[] { sheet }, NoLines, history);
        var triki = events.Single(e => e.EmployeeId == 1);
        Assert.Equal(PresenceEventKind.Retard, triki.Kind);
        Assert.Equal(30, triki.Minutes);
        Assert.Equal(ImpactKind.AvecImpact, triki.Impact);
        Assert.Equal(20, triki.StoppedMinutes);
        Assert.Equal(400, triki.PacksLost);
        Assert.Equal(200, triki.KgLost); // 20 min × 20 paquets/min × 500 g
        Assert.Equal(ImpactKind.SansImpact, events.Single(e => e.EmployeeId == 2).Impact);
        Assert.Equal(ImpactKind.SansMachine, events.Single(e => e.EmployeeId == 3).Impact);
    }

    [Fact]
    public void Absence_is_compensated_when_the_replacement_kept_the_machine_running()
    {
        var history = new FakeHistory();
        // PC01 stopped 06:00-06:40, before the replacement took over at 06:30.
        history.Stops.Add(("PC01", Day.AddHours(6), Day.AddHours(6).AddMinutes(40)));
        var absent = Person(1, "TRIKI", StaffFunction.Conducteur, "PC01", LineStatus.Absent);
        var moez = Person(2, "GUEDDICH", StaffFunction.Emballage, "CC01", LineStatus.Present);
        moez.ReplacesEmployeeId = 1;
        moez.ActingFunction = StaffFunction.Conducteur;
        moez.ActingMachineId = "PC01";
        moez.ActingFrom = Day.AddHours(6).AddMinutes(30);

        var e = PresenceAnalysis.Events(new[] { Sheet(absent, moez) }, NoLines, history).Single(e => e.EmployeeId == 1);
        Assert.Equal(ImpactKind.AvecImpact, e.Impact);
        Assert.Equal(30, e.StoppedMinutes);
        Assert.Equal("GUEDDICH", e.CoveredBy);

        moez.ActingFrom = Day.AddHours(6);
        history.Stops.Clear();
        e = PresenceAnalysis.Events(new[] { Sheet(absent, moez) }, NoLines, history).Single(e => e.EmployeeId == 1);
        Assert.Equal(ImpactKind.Compensee, e.Impact);

        var acting = Assert.Single(PresenceAnalysis.Acting(new[] { Sheet(absent, moez) }, NoLines));
        Assert.True(acting.IsStepUp);
        Assert.Equal("PC01", acting.Post);
        Assert.Equal(8, acting.Hours);
    }

    [Fact]
    public void Machine_that_produced_with_no_one_on_it_is_flagged()
    {
        var history = new FakeHistory();
        history.Producing.Add(("PC01", Day.AddHours(6), Day.AddHours(14)));
        history.Producing.Add(("PC02", Day.AddHours(6), Day.AddHours(14)));
        history.Producing.Add(("CC01", Day.AddHours(6), Day.AddHours(6).AddMinutes(10)));
        var sheet = Sheet(Person(1, "TRIKI", StaffFunction.Conducteur, "PC01", LineStatus.Present));

        var flagged = PresenceAnalysis.Unstaffed(new[] { sheet }, new[] { "PC01", "PC02", "CC01" }, history);
        Assert.Equal("PC02", Assert.Single(flagged).MachineId);
    }

    [Fact]
    public void Repeat_lateness_needs_three_in_thirty_days()
    {
        var history = new FakeHistory();
        var sheets = Enumerable.Range(0, 3).Select(i =>
        {
            var s = Sheet(Person(1, "TRIKI", StaffFunction.Conducteur, "PC01", LineStatus.Present, entryMin: 10));
            s.ProductionDate = Day.AddDays(-i * 5);
            return s;
        }).ToList();
        var events = PresenceAnalysis.Events(sheets, NoLines, history);
        var r = Assert.Single(PresenceAnalysis.Repeats(events, Day));
        Assert.Equal(3, r.Count);
        Assert.Equal(0, r.WithImpact);
        Assert.Empty(PresenceAnalysis.Repeats(events.Skip(1), Day));
    }
}
