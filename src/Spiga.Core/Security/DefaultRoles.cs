namespace Spiga.Core.Security;

// The roles created on first start. Administrators can change every one of
// them except Administrateur, and add their own.
public static class DefaultRoles
{
    public const string AdminCode = "admin";

    private const AppAction V = AppAction.Voir;
    private const AppAction S = AppAction.Saisir;
    private const AppAction A = AppAction.Valider;
    private const AppAction E = AppAction.Exporter;
    private const AppAction X = AppAction.Administrer;

    public static IReadOnlyList<Role> Create() => new[]
    {
        Make(AdminCode, "Administrateur", "Tous les droits. Rôle système, non modifiable.", true, PermissionSet.Full()),
        Make("direction", "Direction", "Consultation de toute l'usine et exports.", false, Grants(
            (Modules.VueEnsemble, V | E), (Modules.Conditionnement, V | E), (Modules.Rendement, V | E), (Modules.Silos, V | E),
            (Modules.Dechets, V | E), (Modules.Qualite, V | E), (Modules.Maintenance, V | E), (Modules.Energie, V | E),
            (Modules.Rh, V | E), (Modules.Objectifs, V), (Modules.Audit, V))),
        Make("resp-production", "Responsable production", "Production, saisies et validation, objectifs de production.", false, Grants(
            (Modules.VueEnsemble, V | E), (Modules.Conditionnement, V | E), (Modules.Rendement, V | E), (Modules.Silos, V | E),
            (Modules.Dechets, V | E), (Modules.Qualite, V), (Modules.Maintenance, V), (Modules.Energie, V),
            (Modules.Rh, V), (Modules.SaisieDechets, V | S | A), (Modules.SaisieQuantites, V | S | A),
            (Modules.Objectifs, V | S), (Modules.Referentiels, V))),
        Make("chef-equipe", "Chef d'équipe", "Suivi de son équipe, saisie et validation des déchets.", false, Grants(
            (Modules.VueEnsemble, V), (Modules.Conditionnement, V | E), (Modules.Rendement, V), (Modules.Silos, V),
            (Modules.Dechets, V), (Modules.SaisieDechets, V | S | A), (Modules.SaisieQuantites, V | S))),
        Make("operateur", "Opérateur de saisie", "Saisie des déchets sur son poste et ses machines.", false, Grants(
            (Modules.Conditionnement, V), (Modules.SaisieDechets, V | S))),
        Make("resp-qualite", "Responsable qualité", "Qualité, conformité des silos.", false, Grants(
            (Modules.VueEnsemble, V), (Modules.Qualite, V | E), (Modules.Silos, V | S | A | E), (Modules.Dechets, V), (Modules.Rendement, V))),
        Make("resp-maintenance", "Responsable maintenance", "Maintenance, arrêts et planification, objectifs maintenance.", false, Grants(
            (Modules.VueEnsemble, V), (Modules.Conditionnement, V), (Modules.Maintenance, V | S | A | E), (Modules.Energie, V),
            (Modules.Objectifs, V | S))),
        Make("resp-rh", "Responsable RH", "Absentéisme, y compris nominatif.", false, Grants(
            (Modules.VueEnsemble, V), (Modules.Rh, V | E), (Modules.RhNominatif, V | E))),
    };

    private static PermissionSet Grants(params (string Module, AppAction Actions)[] grants)
        => new(grants.Select(g => new KeyValuePair<string, AppAction>(g.Module, g.Actions)));

    private static Role Make(string code, string name, string description, bool system, PermissionSet permissions)
        => new() { Code = code, Name = name, Description = description, IsSystem = system, Permissions = permissions };
}
