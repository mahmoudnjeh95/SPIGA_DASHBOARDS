using Spiga.Core.Plant;
using Spiga.Core.Staff;
using Spiga.Core.Waste;
using Spiga.UI.Services;
using SpigaDashboards.App.Models;
using Xunit;

namespace Spiga.Tests;

public class EmployeeProfileTests
{
    private static readonly DateTime Day = new(2026, 11, 2);

    private static AttendanceSheet Sheet(DateTime date, params AttendanceLine[] lines)
    {
        var s = new AttendanceSheet { Id = date.Day, ProductionDate = date, ShiftId = 1, Team = "Equipe B", Status = SheetStatus.Validee };
        foreach (var l in lines)
            s.Lines.Add(l);
        return s;
    }

    private static AttendanceLine Present(DateTime date, int id, StaffFunction f, string machine, int entryHour = 6, int exitHour = 14) => new()
    {
        EmployeeId = id, FullName = $"P{id}", Function = f, HomeTeam = "Equipe B", Status = LineStatus.Present,
        MachineId = machine, EntryAt = date.AddHours(entryHour), ExitAt = date.AddHours(exitHour),
    };

    private static AttendanceLine Absent(DateTime date, int id, AbsenceCode code) => new()
    {
        EmployeeId = id, FullName = $"P{id}", Function = StaffFunction.Emballage, HomeTeam = "Equipe B", Status = LineStatus.Absent, Absence = code,
    };

    // One hour of a machine: 1000 bags of 1 kg (1 t), stopped `stopped` minutes.
    private static ProductionBucket Hour(string machine, DateTime start, double stopped) => new()
    {
        MachineId = machine, StartTime = start, EndTime = start.AddHours(1), ShiftId = Shifts.At(start),
        TotalBags = 1000, BagsInTolerance = 1000, TargetNetWeight = 1000, ObservedMinutes = 60, RunMinutes = 60 - stopped,
    };

    private static PresenceReport.Result Result(IEnumerable<AttendanceSheet> sheets, IEnumerable<ProductionBucket>? lots = null,
        IEnumerable<StopQualification>? qualifications = null)
        => new(Day, Day.AddDays(20), sheets.ToList(), Array.Empty<PresenceEvent>(), Array.Empty<UnstaffedMachine>(),
            Array.Empty<ActingRecord>(), new Dictionary<ActingRecord, ActingPerformance>(), Array.Empty<RepeatLateness>(),
            new Dictionary<int, ProductionLine>(), null)
        {
            Lots = (lots ?? Array.Empty<ProductionBucket>()).ToList(),
            Qualifications = (qualifications ?? Array.Empty<StopQualification>()).ToList(),
        };

    [Fact]
    public void A_replacement_splits_the_poste_between_own_post_and_the_higher_one()
    {
        var line = Present(Day, 1, StaffFunction.Emballage, "PC01");
        line.ReplacesEmployeeId = 2;
        line.ActingFunction = StaffFunction.Conducteur;
        line.ActingMachineId = "PC02";
        line.ActingFrom = Day.AddHours(10);
        var p = EmployeeProfiles.Build(Result(new[] { Sheet(Day, line) }), Array.Empty<WasteEntry>(), new FakePlant()).Single();

        Assert.Equal(2, p.Held.Count);
        Assert.Equal((4.0, false), (p.Held[0].Hours, p.Held[0].StepUp));
        Assert.Equal((4.0, true, "PC02"), (p.Held[1].Hours, p.Held[1].StepUp, p.Held[1].Machines.Single()));
        Assert.Contains(p.Skills, s => s.Function == StaffFunction.Conducteur && s.MachineId == "PC02" && s.Level == SkillLevel.Occasionnel);
        Assert.Equal(2, p.Skills.Count);
    }

    [Fact]
    public void Bradford_counts_spells_of_A_and_M_and_the_rate_leaves_leave_out()
    {
        // Present, A, A (one spell), present, M (second spell), C.
        var codes = new AbsenceCode?[] { null, AbsenceCode.A, AbsenceCode.A, null, AbsenceCode.M, AbsenceCode.C };
        var sheets = codes.Select((c, i) => Sheet(Day.AddDays(i), c is { } code ? Absent(Day.AddDays(i), 1, code) : Present(Day.AddDays(i), 1, StaffFunction.Emballage, "PC01")));
        var a = EmployeeProfiles.Build(Result(sheets), Array.Empty<WasteEntry>(), new FakePlant()).Single().Presence;

        Assert.Equal((2, 2, 1, 1), (a.Present, a.A, a.M, a.C));
        Assert.Equal(2, a.Spells);
        Assert.Equal(2 * 2 * 3, a.Bradford);
        Assert.Equal(40, a.Rate!.Value, 3);
    }

    [Fact]
    public void Stops_and_waste_are_compared_with_the_same_machine_on_the_other_postes()
    {
        // P1 holds PC01 on day 1 (morning), someone else on day 2.
        var day2 = Day.AddDays(1);
        var sheets = new[] { Sheet(Day, Present(Day, 1, StaffFunction.Conducteur, "PC01")), Sheet(day2, Present(day2, 2, StaffFunction.Conducteur, "PC01")) };
        var lots = Enumerable.Range(6, 8).Select(h => Hour("PC01", Day.AddHours(h), 15))
            .Concat(Enumerable.Range(6, 8).Select(h => Hour("PC01", day2.AddHours(h), 3)));
        var reglage = new StopQualification { MachineId = "PC01", StopStart = Day.AddHours(7), StopEnd = Day.AddHours(8), CauseCode = "REGLAGE" };
        var waste = new[]
        {
            new WasteEntry { Kind = WasteKind.Emballage, MachineId = "PC01", ProductionDate = Day, ShiftId = 1, Quantity = 40 },
            new WasteEntry { Kind = WasteKind.Emballage, MachineId = "PC01", ProductionDate = day2, ShiftId = 1, Quantity = 8 },
        };
        var p = EmployeeProfiles.Build(Result(sheets, lots, new[] { reglage }), waste, new FakePlant()).Single(x => x.EmployeeId == 1);

        Assert.Equal(15, p.Own.StopPerHour!.Value, 3);
        Assert.Equal(3, p.Usual.StopPerHour!.Value, 3);
        Assert.Equal(8000, p.Own.Kg, 0);
        Assert.Equal(5, p.WastePerTonne!.Value, 3);
        Assert.Equal(1, p.UsualWastePerTonne!.Value, 3);
        Assert.Equal(60, p.Causes.Single(c => c.Code == "REGLAGE").Minutes, 3);
        Assert.Equal(120 - 60, p.Causes.Single(c => c.Code == null).Minutes, 3);
    }

    // A manutention worker who held PC02 as conducteur on `postes` mornings.
    private static EmployeeProfile ActingProfile(int postes, AbsenceCode? absentOnLastDay = null)
    {
        var sheets = new List<AttendanceSheet>();
        for (var i = 0; i < postes; i++)
        {
            var d = Day.AddDays(i);
            var line = Present(d, 1, StaffFunction.Emballage, "PC01");
            line.ReplacesEmployeeId = 2;
            line.ActingFunction = StaffFunction.Conducteur;
            line.ActingMachineId = "PC02";
            line.ActingFrom = d.AddHours(6);
            sheets.Add(Sheet(d, line));
        }
        if (absentOnLastDay is { } code)
            sheets.Add(Sheet(Day.AddDays(postes), Absent(Day.AddDays(postes), 1, code)));
        return EmployeeProfiles.Build(Result(sheets), Array.Empty<WasteEntry>(), new FakePlant()).Single();
    }

    [Fact]
    public void The_verdict_needs_enough_postes_on_the_next_post_up()
    {
        var once = EmployeeScore.Of(ActingProfile(1));
        Assert.Equal(StaffFunction.Conducteur, once.Target);
        Assert.NotEqual(Readiness.Ready, once.Readiness);
        Assert.Contains(once.Checks, c => !c.Ok && c.Text.Contains("1 fois"));

        var often = EmployeeScore.Of(ActingProfile(EmployeeProfiles.FormedPostes));
        Assert.Contains(often.Checks, c => c.Ok && c.Text.Contains($"{EmployeeProfiles.FormedPostes} fois"));
        Assert.Equal(100, often.Presence.Score);
    }

    [Fact]
    public void Someone_never_tried_higher_is_untested_and_absences_lower_the_presence_score()
    {
        var line = Present(Day, 1, StaffFunction.Emballage, "PC01");
        var never = EmployeeScore.Of(EmployeeProfiles.Build(Result(new[] { Sheet(Day, line) }), Array.Empty<WasteEntry>(), new FakePlant()).Single());
        Assert.Equal(Readiness.Untested, never.Readiness);

        // 5 present, 1 A: 83,3 % present -> 100 - 5 x 16,7.
        var absent = EmployeeScore.Of(ActingProfile(5, AbsenceCode.A));
        Assert.Equal(100 - 5 * (100 - 500.0 / 6), absent.Presence.Score!.Value, 3);
        Assert.Contains(absent.Watch, w => w.Contains("non justifiée"));
    }

    [Fact]
    public void Someone_without_a_machine_has_attendance_but_no_machine_figures()
    {
        var line = Present(Day, 1, StaffFunction.Nettoyage, "PC01");
        line.MachineId = null;
        var p = EmployeeProfiles.Build(Result(new[] { Sheet(Day, line) }), Array.Empty<WasteEntry>(), new FakePlant()).Single();
        Assert.False(p.HoldsMachines);
        Assert.Null(p.Own.Trs);
        Assert.Equal(100, p.Presence.Rate!.Value, 3);
        Assert.Single(p.Skills);
    }
}
