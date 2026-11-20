namespace Spiga.Core.Staff;

// The functions of the packaging service, in the order of the paper
// pointage sheet: the chef de conditionnement first (he fills the sheet),
// then the chefs de ligne, the conducteurs, the packaging and cleaning staff.
public enum StaffFunction
{
    ChefConditionnement,
    ChefLigne,
    Conducteur,
    Emballage,
    Nettoyage,
}

public static class StaffFunctions
{
    public static readonly IReadOnlyList<StaffFunction> All = Enum.GetValues<StaffFunction>();

    public static string Label(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => "Chef de conditionnement",
        StaffFunction.ChefLigne => "Chef de ligne",
        StaffFunction.Conducteur => "Conducteur de ligne",
        StaffFunction.Emballage => "Emballage",
        _ => "Nettoyage",
    };

    public static string Short(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => "Chef cond.",
        StaffFunction.ChefLigne => "Chef de ligne",
        StaffFunction.Conducteur => "Conducteur",
        StaffFunction.Emballage => "Emballage",
        _ => "Nettoyage",
    };

    // Higher = more responsibility. Running the post of a higher function
    // (an emballage worker on a conducteur's machine) is a step up, kept in
    // the polyvalence record.
    public static int Rank(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => 4,
        StaffFunction.ChefLigne => 3,
        StaffFunction.Conducteur => 2,
        StaffFunction.Emballage => 1,
        _ => 0,
    };

    // Conducteurs and emballage staff hold one machine; a chef de ligne holds
    // a ligne (a set of machines); the others have no machine.
    public static bool HoldsMachine(StaffFunction f) => f is StaffFunction.Conducteur or StaffFunction.Emballage;

    public static bool HoldsLine(StaffFunction f) => f == StaffFunction.ChefLigne;
}

// A ligne: the machines a chef de ligne is in charge of.
public sealed class ProductionLine
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Machines { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

// Where an employee works from a date on: team, function and post (machine
// or ligne). A transfer, a promotion or a new machine adds a posting; the
// earlier ones stay, so past sheets and rates keep their meaning.
public sealed class EmployeePosting
{
    public long Id { get; set; }
    public int EmployeeId { get; set; }
    public DateTime ValidFrom { get; set; }
    public string Team { get; set; } = "";
    public StaffFunction Function { get; set; }
    public string? MachineId { get; set; }
    public int? LineId { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class Employee
{
    public int Id { get; set; }
    public string Matricule { get; set; } = "";
    public string FullName { get; set; } = "";
    public bool IsActive { get; set; } = true;

    // Oldest first.
    public List<EmployeePosting> Postings { get; set; } = new();

    // The posting in force on that day, null = not yet employed then.
    public EmployeePosting? PostingAt(DateTime date)
        => Postings.Where(p => p.ValidFrom.Date <= date.Date).OrderBy(p => p.ValidFrom).ThenBy(p => p.Id).LastOrDefault();

    public EmployeePosting? Current(DateTime today) => PostingAt(today) ?? Postings.OrderBy(p => p.ValidFrom).FirstOrDefault();
}

// What the employee form sends.
public sealed record EmployeeDraft(string FullName, string Matricule);

public sealed record PostingDraft(DateTime ValidFrom, string Team, StaffFunction Function, string? MachineId, int? LineId);

public interface IStaffStore
{
    Task<IReadOnlyList<Employee>> ListEmployeesAsync();
    Task<Employee?> GetEmployeeAsync(int id);
    Task<int> InsertEmployeeAsync(Employee employee, EmployeePosting posting);
    Task UpdateEmployeeAsync(Employee employee);

    // Replaces the posting of the same employee and day, if any.
    Task SavePostingAsync(EmployeePosting posting);
    Task DeletePostingAsync(long id);

    Task<IReadOnlyList<ProductionLine>> ListLinesAsync();
    Task<int> SaveLineAsync(ProductionLine line);
    Task DeleteLineAsync(int id);
}
