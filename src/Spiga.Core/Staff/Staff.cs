namespace Spiga.Core.Staff;

// The functions of the packaging service, in the order of the paper
// pointage sheet: the chef de conditionnement first (he fills the sheet),
// then the chefs d'équipe, the conducteurs, the ouvriers de manutention, de
// nettoyage and de mise en carton. The names are the ones stored in the
// database: the labels below are what the screens show.
public enum StaffFunction
{
    ChefConditionnement,
    ChefLigne,          // Chef d'équipe
    Conducteur,
    Emballage,          // Ouvrier de manutention
    Nettoyage,          // Ouvrier de nettoyage
    MiseEnCarton,       // Ouvrier de mise en carton
}

public static class StaffFunctions
{
    public static readonly IReadOnlyList<StaffFunction> All = Enum.GetValues<StaffFunction>();

    public static string Label(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => "Chef de conditionnement",
        StaffFunction.ChefLigne => "Chef d'équipe",
        StaffFunction.Conducteur => "Conducteur",
        StaffFunction.Emballage => "Ouvrier de manutention",
        StaffFunction.MiseEnCarton => "Ouvrier de mise en carton",
        _ => "Ouvrier de nettoyage",
    };

    // The heading of a group of rows on the pointage sheet.
    public static string Plural(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => "Chef de conditionnement",
        StaffFunction.ChefLigne => "Chefs d'équipe",
        StaffFunction.Conducteur => "Conducteurs",
        StaffFunction.Emballage => "Ouvriers de manutention",
        StaffFunction.MiseEnCarton => "Ouvriers de mise en carton",
        _ => "Ouvriers de nettoyage",
    };

    public static string Short(StaffFunction f) => f switch
    {
        StaffFunction.ChefConditionnement => "Chef cond.",
        StaffFunction.ChefLigne => "Chef d'équipe",
        StaffFunction.Conducteur => "Conducteur",
        StaffFunction.Emballage => "Manutention",
        StaffFunction.MiseEnCarton => "Mise en carton",
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

    // Conducteurs and ouvriers de manutention hold one machine; a chef d'équipe holds
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

    // "Ligne Couscous 1", whether or not the name already says "Ligne".
    public string Label => LabelOf(Name);

    public static string LabelOf(string? name)
        => string.IsNullOrWhiteSpace(name) ? "Ligne"
            : name.TrimStart().StartsWith("ligne", StringComparison.CurrentCultureIgnoreCase) ? name.Trim() : "Ligne " + name.Trim();
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

    // An inactive employee left on this day: he is still on the sheets of
    // the days before (a past poste opened later keeps him). Null for an
    // active one.
    public DateTime? InactiveFrom { get; set; }

    public bool ActiveOn(DateTime date) => IsActive || InactiveFrom is { } left && date.Date < left.Date;

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
