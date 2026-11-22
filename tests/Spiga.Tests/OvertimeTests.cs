using Spiga.Core.Audit;
using Spiga.Core.Security;
using Spiga.Core.Staff;

namespace Spiga.Tests;

// Heures supplémentaires. The test plant: Matin = Equipe B, Après-midi and
// Nuit = Equipe A; now = 17/11/2026 13:30 (the morning can be validated).
public class OvertimeTests
{
    private static readonly DateTime Today = new(2026, 11, 17);

    private sealed record Setup(TestApp App, Actor Resp, Actor ChefB, Actor ChefA, Dictionary<string, Employee> People)
    {
        public int this[string name] => People[name].Id;
    }

    private static async Task<Setup> TeamAsync(TestApp app)
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
        await Add("HAJ BRAHIM Makrem", "Equipe B", StaffFunction.ChefConditionnement);
        await Add("TRIKI Faez", "Equipe B", StaffFunction.Conducteur, "PC01");
        await Add("GUEDDICH Moez", "Equipe B", StaffFunction.Emballage, "CC01");
        await Add("AYADI Khalil", "Equipe A", StaffFunction.Conducteur, "PL01");
        return new Setup(app, resp, chefB, chefA, people);
    }

    private static AttendanceLine Line(AttendanceSheet s, Setup t, string name) => s.Lines.Single(l => l.EmployeeId == t[name]);

    private static void ConfirmAll(AttendanceSheet s)
    {
        foreach (var l in s.Lines.Where(l => l.Status == LineStatus.AConfirmer))
            l.Status = LineStatus.Present;
    }

    // Team B's morning of `date`, everyone present, TRIKI out at `trikiOut`.
    private static async Task<AttendanceSheet> MorningAsync(Setup t, DateTime date, DateTime? trikiOut = null)
    {
        var sheet = await t.App.Attendance.OpenAsync(t.ChefB, date, 1);
        ConfirmAll(sheet);
        if (trikiOut != null)
            Line(sheet, t, "TRIKI Faez").ExitAt = trikiOut;
        return sheet;
    }

    [Fact]
    public async Task Only_the_production_manager_manages_overtime()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        Assert.False(app.Overtime.CanManage(t.ChefB));
        Assert.True(app.Overtime.CanManage(t.Resp));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Overtime.BanAsync(t.ChefB, t["TRIKI Faez"], "test"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Overtime.SetLimitAsync(t.ChefB, 10));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.Overtime.MonthAsync(t.ChefB, Today));
    }

    [Fact]
    public async Task A_forbidden_person_is_pointed_at_the_poste_hours_only()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        await app.Overtime.BanAsync(t.Resp, t["TRIKI Faez"], "trop d'heures en octobre");
        Assert.Contains(await app.AuditAsync(), e => e.Action == AuditActions.OvertimeBanned && e.Target == "TRIKI Faez");

        var sheet = await MorningAsync(t, Today, Today.AddHours(15));
        var book = await app.Attendance.OvertimeAsync(t.ChefB, sheet);
        var state = book.State(t["TRIKI Faez"], Today, sheet);
        Assert.True(state.Restricted);
        Assert.Equal(0, state.Remaining);
        Assert.Contains("trop d'heures en octobre", state.Detail);
        var problem = Assert.Single(book.Problems(sheet));
        Assert.Equal(t["TRIKI Faez"], problem.EmployeeId);
        Assert.Contains("interdit d'heures supplémentaires", problem.Message);
        Assert.Contains("06:00 – 14:00", problem.Message);
        // Not even a draft.
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, null));

        // Arriving early is overtime too.
        Line(sheet, t, "TRIKI Faez").ExitAt = Today.AddHours(14);
        Line(sheet, t, "TRIKI Faez").EntryAt = Today.AddHours(5);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null));

        Line(sheet, t, "TRIKI Faez").EntryAt = Today.AddHours(6);
        await app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null);
        // The others are not concerned.
        Assert.False(book.State(t["GUEDDICH Moez"], Today, sheet).Restricted);

        // Lifted: allowed again.
        var ban = Assert.Single(await app.Overtime.DecisionsAsync(t.Resp));
        await app.Overtime.LiftAsync(t.Resp, ban.Id);
        var again = await MorningAsync(t, Today, Today.AddHours(15));
        await app.Attendance.SaveAsync(t.ChefB, again, validate: false, "resté une heure de plus");
    }

    [Fact]
    public async Task A_sheet_saved_before_the_ban_can_still_be_corrected()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var sheet = await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, Today, Today.AddHours(16)), validate: true, null);
        await app.Overtime.BanAsync(t.Resp, t["TRIKI Faez"], "");

        // Correcting someone else keeps TRIKI's 2 h; adding to them is refused.
        Line(sheet, t, "GUEDDICH Moez").EntryAt = Today.AddHours(6).AddMinutes(30);
        await app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, "retard oublié");
        sheet = (await app.Attendance.OpenAsync(t.ChefB, Today, 1));
        Line(sheet, t, "TRIKI Faez").ExitAt = Today.AddHours(17);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, "plus tard"));
        Line(sheet, t, "TRIKI Faez").ExitAt = Today.AddHours(15);
        await app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, "moins");
    }

    [Fact]
    public async Task Reaching_the_monthly_limit_forbids_overtime_until_a_derogation()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        await app.Overtime.SetLimitAsync(t.Resp, 3);
        Assert.Equal(3, (await app.Overtime.RuleAsync()).MonthlyLimitHours);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Overtime.SetLimitAsync(t.Resp, -1));

        // 2 h yesterday.
        await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, Today.AddDays(-1), Today.AddDays(-1).AddHours(16)), validate: true, null);

        // 1 h 30 today would make 3 h 30: refused, 1 h left.
        var sheet = await MorningAsync(t, Today, Today.AddHours(15).AddMinutes(30));
        var book = await app.Attendance.OvertimeAsync(t.ChefB, sheet);
        var state = book.State(t["TRIKI Faez"], Today, sheet);
        Assert.Equal(120, state.MonthMinutes);
        Assert.Equal(60, state.Remaining);
        Assert.False(state.Restricted);
        Assert.Contains("1 h d'heures sup. au plus", Assert.Single(book.Problems(sheet)).Message);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null));

        // 1 h: exactly the limit.
        Line(sheet, t, "TRIKI Faez").ExitAt = Today.AddHours(15);
        sheet = await app.Attendance.SaveAsync(t.ChefB, sheet, validate: true, null);

        // Reached: forbidden, shown as such for the next sheets of the month.
        var tomorrowBook = await app.Overtime.BookAsync(Today.AddDays(1));
        var reached = tomorrowBook.State(t["TRIKI Faez"], Today.AddDays(1));
        Assert.True(reached.LimitReached);
        Assert.Equal("Interdit · limite atteinte", reached.Label);
        var (_, rows) = await app.Overtime.MonthAsync(t.Resp, Today);
        var row = rows.First();
        Assert.Equal(t["TRIKI Faez"], row.Employee.Id);
        Assert.Equal(180, row.MonthMinutes);
        Assert.Equal(2, row.Days.Count);
        Assert.True(row.State.LimitReached);
        // A new month starts from zero.
        Assert.False((await app.Overtime.BookAsync(Today.AddMonths(1))).State(t["TRIKI Faez"], Today.AddMonths(1)).Restricted);

        // A dérogation (with a reason) lets him go over this month.
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today, ""));
        await app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today, "inventaire de fin de mois");
        sheet = await app.Attendance.OpenAsync(t.ChefB, Today, 1);
        Line(sheet, t, "TRIKI Faez").ExitAt = Today.AddHours(17);
        await app.Attendance.SaveAsync(t.ChefB, sheet, validate: false, "inventaire");
    }

    [Fact]
    public async Task A_second_poste_or_a_rest_day_is_overtime_a_transfer_is_not()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var yesterday = Today.AddDays(-1);

        // GUEDDICH (team B, morning) did his morning, then the afternoon with team A: 8 h of overtime.
        await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, yesterday), validate: true, null);
        var afternoon = await app.Attendance.OpenAsync(t.ChefA, yesterday, 2);
        ConfirmAll(afternoon);
        var renfort = await app.Attendance.RenfortLineAsync(afternoon, t["GUEDDICH Moez"]);
        renfort.Status = LineStatus.Present;
        afternoon.Lines.Add(renfort);
        await app.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null);
        var book = await app.Overtime.BookAsync(yesterday);
        Assert.Equal(480, book.MonthMinutes(t["GUEDDICH Moez"]));
        Assert.Equal(0, book.MonthMinutes(t["TRIKI Faez"]));

        // Forbidden, TRIKI cannot be lent for the afternoon after his morning.
        await app.Overtime.BanAsync(t.Resp, t["TRIKI Faez"], "", yesterday);
        afternoon = await app.Attendance.OpenAsync(t.ChefA, yesterday, 2);
        var candidate = (await app.Attendance.RenfortCandidatesAsync(t.ChefA, afternoon)).Single(c => c.Employee.Id == t["TRIKI Faez"]);
        var why = (await app.Attendance.OvertimeAsync(t.ChefA, afternoon)).RenfortRefusal(afternoon, candidate.Employee, candidate.Posting);
        Assert.NotNull(why);
        Assert.Contains("déjà fait son poste", why);
        var line = await app.Attendance.RenfortLineAsync(afternoon, t["TRIKI Faez"]);
        line.Status = LineStatus.Present;
        afternoon.Lines.Add(line);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null));
    }

    [Fact]
    public async Task A_forbidden_person_can_be_moved_to_another_poste_instead_of_his_own()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var yesterday = Today.AddDays(-1);
        await app.Overtime.BanAsync(t.Resp, t["GUEDDICH Moez"], "", yesterday);

        // Team B works the morning; GUEDDICH works the afternoon with team A instead.
        var afternoon = await app.Attendance.OpenAsync(t.ChefA, yesterday, 2);
        ConfirmAll(afternoon);
        var renfort = await app.Attendance.RenfortLineAsync(afternoon, t["GUEDDICH Moez"]);
        renfort.Status = LineStatus.Present;
        afternoon.Lines.Add(renfort);
        Assert.Empty((await app.Attendance.OvertimeAsync(t.ChefA, afternoon)).Problems(afternoon));
        await app.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null);

        // His own morning sheet shows him away, and validates.
        var morning = await app.Attendance.OpenAsync(t.ChefB, yesterday, 1);
        Assert.Equal(LineStatus.Ailleurs, Line(morning, t, "GUEDDICH Moez").Status);
        ConfirmAll(morning);
        await app.Attendance.SaveAsync(t.ChefB, morning, validate: true, null);
        Assert.Equal(0, (await app.Overtime.BookAsync(yesterday)).MonthMinutes(t["GUEDDICH Moez"]));

        // Pointed at his morning too, it would be a second poste: refused.
        morning = await app.Attendance.OpenAsync(t.ChefB, yesterday, 1);
        var g = Line(morning, t, "GUEDDICH Moez");
        g.Status = LineStatus.Present;
        g.EntryAt = morning.Start;
        g.ExitAt = morning.End;
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Attendance.SaveAsync(t.ChefB, morning, validate: false, "erreur"));
    }

    [Fact]
    public async Task The_month_shows_each_persons_overtime()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, Today, Today.AddHours(15).AddMinutes(30)), validate: true, null);
        await app.Overtime.BanAsync(t.Resp, t["GUEDDICH Moez"], "santé");

        var (_, rows) = await app.Overtime.MonthAsync(t.Resp, Today);
        Assert.Equal(4, rows.Count);
        Assert.Equal("TRIKI Faez", rows[0].Employee.FullName);
        Assert.Equal(90, rows[0].MonthMinutes);
        Assert.Equal("1 h 30", Overtime.Hours(rows[0].MonthMinutes));
        Assert.Equal(90, rows[0].Days[Today]);
        var g = rows.Single(r => r.Employee.Id == t["GUEDDICH Moez"]);
        Assert.Equal("Interdit d'heures sup.", g.State.Label);
    }

    // Team B's morning of the 16th with GUEDDICH absent (code), validated;
    // then team A's afternoon with him present as renfort.
    private static async Task<(AttendanceSheet Morning, AttendanceSheet Afternoon)> MovedAsync(Setup t, AbsenceCode code)
    {
        var day = Today.AddDays(-1);
        var morning = await MorningAsync(t, day);
        var g = Line(morning, t, "GUEDDICH Moez");
        g.Status = LineStatus.Absent;
        g.Absence = code;
        await t.App.Attendance.SaveAsync(t.ChefB, morning, validate: true, null);
        var afternoon = await t.App.Attendance.OpenAsync(t.ChefA, day, 2);
        ConfirmAll(afternoon);
        var renfort = await t.App.Attendance.RenfortLineAsync(afternoon, t["GUEDDICH Moez"]);
        renfort.Status = LineStatus.Present;
        afternoon.Lines.Add(renfort);
        afternoon = await t.App.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null);
        return (await t.App.Attendance.OpenAsync(t.ChefB, day, 1), afternoon);
    }

    [Theory]
    [InlineData(AbsenceCode.A)]
    [InlineData(AbsenceCode.M)]
    [InlineData(AbsenceCode.C)]
    public async Task Absent_on_his_poste_and_present_on_another_the_same_day_is_a_transfer(AbsenceCode code)
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var (morning, afternoon) = await MovedAsync(t, code);

        var g = Line(morning, t, "GUEDDICH Moez");
        Assert.Equal(LineStatus.Ailleurs, g.Status);
        Assert.Equal(code, g.Absence);
        Assert.Equal(SheetStatus.Validee, morning.Status);
        var history = await app.Attendance.HistoryAsync(t.ChefB, morning);
        Assert.Equal(AuditActions.SheetTransfer, history[^1].Action);
        Assert.Contains($"GUEDDICH Moez : {code} ({AbsenceCodes.Label(code)}) → transféré au poste Après-midi du 16/11 · Equipe A", history[^1].Changes);
        Assert.Contains(await app.AuditAsync(), e => e.Action == AuditActions.SheetTransfer);
        // Not overtime, and the morning sheet still saves.
        Assert.Equal(0, (await app.Overtime.BookAsync(morning.ProductionDate)).MonthMinutes(t["GUEDDICH Moez"]));
        Line(morning, t, "TRIKI Faez").EntryAt = morning.Start.AddMinutes(10);
        morning = await app.Attendance.SaveAsync(t.ChefB, morning, validate: false, "retard");
        Assert.Equal(LineStatus.Ailleurs, Line(morning, t, "GUEDDICH Moez").Status);

        // Taken off the afternoon: absent again, with his code.
        afternoon.Lines.RemoveAll(l => l.EmployeeId == t["GUEDDICH Moez"]);
        await app.Attendance.SaveAsync(t.ChefA, afternoon, validate: false, "erreur de personne");
        morning = await app.Attendance.OpenAsync(t.ChefB, morning.ProductionDate, 1);
        Assert.Equal(LineStatus.Absent, Line(morning, t, "GUEDDICH Moez").Status);
        Assert.Equal(code, Line(morning, t, "GUEDDICH Moez").Absence);
    }

    [Fact]
    public async Task An_accident_de_travail_is_not_turned_into_a_transfer()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var (morning, afternoon) = await MovedAsync(t, AbsenceCode.AT);
        Assert.Equal(LineStatus.Absent, Line(morning, t, "GUEDDICH Moez").Status);
        Assert.Equal(AbsenceCode.AT, Line(morning, t, "GUEDDICH Moez").Absence);
        var at = await app.Attendance.AccidentElsewhereAsync(t.ChefA, afternoon);
        Assert.Equal(1, at[t["GUEDDICH Moez"]].ShiftId);
    }

    [Fact]
    public async Task Pointed_absent_after_the_other_poste_he_is_transferred_at_once()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var day = Today.AddDays(-1);
        var afternoon = await app.Attendance.OpenAsync(t.ChefA, day, 2);
        ConfirmAll(afternoon);
        var renfort = await app.Attendance.RenfortLineAsync(afternoon, t["GUEDDICH Moez"]);
        renfort.Status = LineStatus.Present;
        afternoon.Lines.Add(renfort);
        await app.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null);

        var morning = await MorningAsync(t, day);
        var g = Line(morning, t, "GUEDDICH Moez");
        g.Status = LineStatus.Absent;
        g.Absence = AbsenceCode.M;
        morning = await app.Attendance.SaveAsync(t.ChefB, morning, validate: true, null);
        Assert.Equal(LineStatus.Ailleurs, Line(morning, t, "GUEDDICH Moez").Status);
        Assert.Equal(AbsenceCode.M, Line(morning, t, "GUEDDICH Moez").Absence);
    }

    [Fact]
    public async Task A_derogation_is_given_only_once_the_limit_is_reached_this_month()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);

        // No limit: nothing to go over.
        var none = await Assert.ThrowsAsync<RuleViolationException>(() => app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today, "inventaire"));
        Assert.Contains("Aucune limite", none.Message);

        await app.Overtime.SetLimitAsync(t.Resp, 2);
        var early = await Assert.ThrowsAsync<RuleViolationException>(() => app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today, "inventaire"));
        Assert.Contains("une fois la limite atteinte", early.Message);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today.AddMonths(1), "inventaire"));

        // 2 h yesterday: reached, the dérogation can be given.
        await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, Today.AddDays(-1), Today.AddDays(-1).AddHours(16)), validate: true, null);
        await app.Overtime.AllowOverLimitAsync(t.Resp, t["TRIKI Faez"], Today, "inventaire");
    }

    [Fact]
    public async Task Refusal_messages_end_with_a_single_period()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        await app.Overtime.BanAsync(t.Resp, t["GUEDDICH Moez"], "santé");
        // GUEDDICH (team B, morning) as renfort in team A's afternoon: a second poste.
        await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, Today), validate: true, null);
        app.Clock.Now = Today.AddHours(15);
        var afternoon = await app.Attendance.OpenAsync(t.ChefA, Today, 2);
        var book = await app.Attendance.OvertimeAsync(t.ChefA, afternoon);
        var why = book.RenfortRefusal(afternoon, t.People["GUEDDICH Moez"], t.People["GUEDDICH Moez"].Postings[0]);
        Assert.NotNull(why);
        Assert.EndsWith("heures supplémentaires.", why);
        Assert.DoesNotContain("..", why);
    }

    [Fact]
    public async Task The_presence_calendar_counts_overtime_like_the_overtime_screen()
    {
        using var app = new TestApp();
        var t = await TeamAsync(app);
        var day = Today.AddDays(-1);
        // TRIKI stays to 16:00; GUEDDICH works his morning, then team A's afternoon as renfort.
        var morning = await app.Attendance.SaveAsync(t.ChefB, await MorningAsync(t, day, day.AddHours(16)), validate: true, null);
        var afternoon = await app.Attendance.OpenAsync(t.ChefA, day, 2);
        ConfirmAll(afternoon);
        var renfort = await app.Attendance.RenfortLineAsync(afternoon, t["GUEDDICH Moez"]);
        renfort.Status = LineStatus.Present;
        afternoon.Lines.Add(renfort);
        afternoon = await app.Attendance.SaveAsync(t.ChefA, afternoon, validate: true, null);

        var entries = Spiga.UI.Services.PresenceCalendar.Entries(new[] { morning, afternoon }, new FakePlant());
        double Of(string name, AttendanceSheet s) => entries.Single(e => e.Sheet.Id == s.Id && e.Line.EmployeeId == t[name]).OvertimeMinutes;
        Assert.Equal(120, Of("TRIKI Faez", morning));
        Assert.Equal(0, Of("GUEDDICH Moez", morning));
        Assert.Equal(480, Of("GUEDDICH Moez", afternoon));
        Assert.Contains(Spiga.UI.Services.CalKind.HeuresSup, entries.Single(e => e.Sheet.Id == afternoon.Id && e.Line.EmployeeId == t["GUEDDICH Moez"]).Kinds);

        var book = await app.Overtime.BookAsync(day);
        foreach (var name in new[] { "TRIKI Faez", "GUEDDICH Moez", "HAJ BRAHIM Makrem", "AYADI Khalil" })
            Assert.Equal(book.MonthMinutes(t[name]), entries.Where(e => e.Line.EmployeeId == t[name]).Sum(e => e.OvertimeMinutes));
    }
}
