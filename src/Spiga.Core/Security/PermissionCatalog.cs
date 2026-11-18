namespace Spiga.Core.Security;

// One right a role can hold: "<module>:<name>". Every permission of a module
// implies its "voir" (seeing the module); removing "voir" removes the
// module's other permissions.
public sealed record PermissionDef(string Key, string Module, string Label, string Description)
{
    public bool IsView => Key.EndsWith(":voir", StringComparison.Ordinal);
}

// The permissions named in the code, for the checks of the services.
public static class Perm
{
    public const string DechetsSaisir = "saisie-dechets:saisir";
    public const string DechetsValider = "saisie-dechets:valider";
    public const string DechetsCorriger = "saisie-dechets:corriger";
    public const string DechetsTardif = "saisie-dechets:tardive";
    public const string DechetsAutrui = "saisie-dechets:autrui";

    public const string ComptesGerer = "utilisateurs:comptes";
    public const string ComptesDebloquer = "utilisateurs:deblocage";
    public const string RolesGerer = "utilisateurs:roles";
    public const string SecuriteGerer = "utilisateurs:securite";
    public const string PlateformesGerer = "utilisateurs:plateformes";

    public const string TableauDetaille = "conditionnement:tableau";
    public const string EcranTv = "conditionnement:tv";
    // The note shown on every screen of the site: over the whole plant.
    public const string NoteEcran = "conditionnement:note";
    public const string ParametresPoste = "parametres:administrer";

    // Responsables: cover several teams (or all). Without it an account is
    // bound to one team and sees the postes of that team only.
    public const string EquipesMultiples = "vue-ensemble:equipes";

    public static string View(string module) => module + ":voir";
}

// Every permission of the application, by module. Adding a feature = adding
// its permission here; roles are then built from these in the application,
// by the administrators, without code.
public static class PermissionCatalog
{
    public static readonly IReadOnlyList<PermissionDef> All = Build();

    private static readonly Dictionary<string, PermissionDef> ByKey = All.ToDictionary(p => p.Key);

    public static PermissionDef? Find(string key) => ByKey.GetValueOrDefault(key);

    public static IEnumerable<PermissionDef> ForModule(string module) => All.Where(p => p.Module == module);

    // The permission behind a usual verb, or null when the module has no such verb.
    public static string? Key(string module, AppAction action)
    {
        var key = $"{module}:{AppActions.Verb(action)}";
        return ByKey.ContainsKey(key) ? key : null;
    }

    public static string Label(string key)
    {
        var def = Find(key);
        if (def == null)
            return key;
        return $"{ModuleCatalog.Find(def.Module)?.Label ?? def.Module} › {def.Label}";
    }

    private static List<PermissionDef> Build()
    {
        var list = new List<PermissionDef>();
        void Add(string module, string name, string label, string description = "")
            => list.Add(new PermissionDef($"{module}:{name}", module, label, description));
        void View(string module, string label = "Consulter") => Add(module, "voir", label);
        void Export(string module) => Add(module, "exporter", "Exporter", "Excel / PDF");

        View(Modules.VueEnsemble); Export(Modules.VueEnsemble);
        Add(Modules.VueEnsemble, "equipes", "Couvrir plusieurs équipes",
            "Responsables. Sans ce droit, le compte est rattaché à une seule équipe et ne voit que ses postes");

        View(Modules.Conditionnement); Export(Modules.Conditionnement);
        Add(Modules.Conditionnement, "tableau", "Tableau de bord détaillé", "Diapositives, historique, synthèse secteur (application poste)");
        Add(Modules.Conditionnement, "tv", "Lancer l'écran TV");
        Add(Modules.Conditionnement, "note", "Modifier la note de l'écran", "Bandeau affiché sur tous les écrans du site : sur toute l'usine");

        View(Modules.Rendement); Export(Modules.Rendement);

        View(Modules.Silos);
        Add(Modules.Silos, "saisir", "Mettre à jour niveau et produit", "Saisie manuelle quand la donnée n'est pas automatique");
        Add(Modules.Silos, "valider", "Déclarer la conformité", "Conforme / non conforme / en attente");
        Export(Modules.Silos);

        View(Modules.Dechets); Export(Modules.Dechets);
        View(Modules.Qualite); Export(Modules.Qualite);

        View(Modules.Maintenance);
        Add(Modules.Maintenance, "saisir", "Qualifier les arrêts, planifier", "Planification, plans de maintenance");
        Add(Modules.Maintenance, "valider", "Valider les interventions");
        Export(Modules.Maintenance);

        View(Modules.Energie); Export(Modules.Energie);

        View(Modules.Rh, "Taux d'absence par équipe"); Export(Modules.Rh);
        View(Modules.RhNominatif, "Taux d'absence par ouvrier"); Export(Modules.RhNominatif);

        View(Modules.SaisieDechets, "Consulter les saisies");
        Add(Modules.SaisieDechets, "saisir", "Saisir", "Créer et modifier ses brouillons");
        Add(Modules.SaisieDechets, "valider", "Valider les saisies d'autrui");
        Add(Modules.SaisieDechets, "corriger", "Corriger une saisie validée", "Avec un motif, tracé au journal");
        Add(Modules.SaisieDechets, "autrui", "Modifier ou supprimer le brouillon d'un autre");
        Add(Modules.SaisieDechets, "tardive", "Saisir au-delà de 7 jours");

        View(Modules.SaisieQuantites, "Consulter les saisies");
        Add(Modules.SaisieQuantites, "saisir", "Saisir");
        Add(Modules.SaisieQuantites, "valider", "Valider les saisies d'autrui");

        View(Modules.Objectifs); Add(Modules.Objectifs, "saisir", "Modifier les objectifs");
        View(Modules.Referentiels); Add(Modules.Referentiels, "saisir", "Modifier les référentiels");

        View(Modules.Utilisateurs, "Consulter comptes et rôles");
        Add(Modules.Utilisateurs, "comptes", "Créer et modifier des comptes", "Limité aux rôles attribuables et à son propre périmètre, sauf avec « Gérer les rôles »");
        Add(Modules.Utilisateurs, "deblocage", "Débloquer, réinitialiser un mot de passe");
        Add(Modules.Utilisateurs, "roles", "Gérer les rôles", "Créer, modifier, supprimer des rôles ; attribuer tout rôle");
        Add(Modules.Utilisateurs, "plateformes", "Gérer les plateformes", "Créer, modifier, supprimer les plateformes (groupes de machines) sur lesquelles les rôles sont donnés");
        Add(Modules.Utilisateurs, "securite", "Modifier les règles de sécurité");

        View(Modules.Sources); Add(Modules.Sources, "administrer", "Configurer les connexions");
        View(Modules.Audit); Export(Modules.Audit);
        View(Modules.Parametres); Add(Modules.Parametres, "administrer", "Modifier les paramètres du poste", "Connexion base, écran TV, cadences");
        return list;
    }
}
