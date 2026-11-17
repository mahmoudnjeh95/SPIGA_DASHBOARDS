namespace Spiga.Core.Security;

// Permission units. A role grants actions per module; several screens can
// share one module (Maintenance: indicateurs, arrêts, pièces).
public static class Modules
{
    public const string VueEnsemble = "vue-ensemble";
    public const string Conditionnement = "conditionnement";
    public const string Rendement = "rendement";
    public const string Silos = "silos";
    public const string Dechets = "dechets";
    public const string Qualite = "qualite";
    public const string Maintenance = "maintenance";
    public const string Energie = "energie";
    public const string Rh = "rh";
    public const string RhNominatif = "rh-nominatif";
    public const string SaisieDechets = "saisie-dechets";
    public const string SaisieQuantites = "saisie-quantites";
    public const string Objectifs = "objectifs";
    public const string Referentiels = "referentiels";
    public const string Utilisateurs = "utilisateurs";
    public const string Sources = "sources";
    public const string Audit = "audit";
    public const string Parametres = "parametres";
}

// A module groups permissions (PermissionCatalog) and screens (Navigation).
public sealed record ModuleInfo(string Key, string Label, string Domain, string Description);

// One entry of the navigation rail. Ready = the screen exists in this
// version; the others open a page that says what the screen will hold.
// Shown when the user holds Permission (default: the module's "voir");
// DesktopOnly entries only in the Windows application.
public sealed record NavItem(
    string Key, string Label, string? Group, string Module, string Route, bool Ready,
    string Content = "", string Filters = "", string Origin = "", string? Permission = null, bool DesktopOnly = false)
{
    public string RequiredPermission => Permission ?? Perm.View(Module);
}

public static class ModuleCatalog
{
    public static readonly IReadOnlyList<ModuleInfo> All = new[]
    {
        new ModuleInfo(Modules.VueEnsemble, "Vue d'ensemble", "Tableaux de bord", "Synthèse usine et points à traiter."),
        new ModuleInfo(Modules.Conditionnement, "Conditionnement", "Production", "TRS, écart de poids, production par machine."),
        new ModuleInfo(Modules.Rendement, "Rendement matière", "Production", "Rendement par secteur et usine."),
        new ModuleInfo(Modules.Silos, "Silos", "Production", "Niveaux, produits et conformité ; Saisir = mise à jour manuelle, Valider = conformité."),
        new ModuleInfo(Modules.Dechets, "Déchets (tableau de bord)", "Production", "Déchets emballage et produit consolidés."),
        new ModuleInfo(Modules.Qualite, "Qualité", "Qualité", "Humidité par produit et par ligne."),
        new ModuleInfo(Modules.Maintenance, "Maintenance", "Maintenance", "Indicateurs, arrêts, planification, pièces et consommables."),
        new ModuleInfo(Modules.Energie, "Énergie", "Énergie", "Électricité et vapeur par tonne."),
        new ModuleInfo(Modules.Rh, "Ressources humaines (agrégé)", "Ressources humaines", "Taux d'absence par équipe."),
        new ModuleInfo(Modules.RhNominatif, "Ressources humaines (nominatif)", "Ressources humaines", "Taux d'absence par ouvrier. Donnée personnelle."),
        new ModuleInfo(Modules.SaisieDechets, "Saisie des déchets", "Saisie", "Déchets emballage et produit."),
        new ModuleInfo(Modules.SaisieQuantites, "Saisie quantités et relevés", "Saisie", "Quantités du rendement, relevés énergie."),
        new ModuleInfo(Modules.Objectifs, "Objectifs", "Administration", "Cibles et objectifs de coûts et consommations."),
        new ModuleInfo(Modules.Referentiels, "Référentiels", "Administration", "Machines, produits, équipes, motifs."),
        new ModuleInfo(Modules.Utilisateurs, "Utilisateurs et rôles", "Administration", "Comptes, rôles, règles de sécurité."),
        new ModuleInfo(Modules.Sources, "Sources de données", "Administration", "Connexions aux systèmes de l'usine."),
        new ModuleInfo(Modules.Audit, "Journal d'audit", "Administration", "Traçabilité des actions."),
        new ModuleInfo(Modules.Parametres, "Paramètres", "Administration", "Paramètres du poste et de l'affichage TV."),
    };

    public static ModuleInfo? Find(string key) => All.FirstOrDefault(m => m.Key == key);

    public static readonly IReadOnlyList<NavItem> Navigation = new[]
    {
        new NavItem("vue-ensemble", "Vue d'ensemble", null, Modules.VueEnsemble, "", true),

        new NavItem("conditionnement", "Conditionnement", "Production", Modules.Conditionnement, "production/conditionnement", true),
        new NavItem("tableau", "Tableau de bord", "Production", Modules.Conditionnement, "production/tableau-de-bord", true,
            Permission: Perm.TableauDetaille, DesktopOnly: true),
        new NavItem("rendement", "Rendement matière", "Production", Modules.Rendement, "module/rendement", false,
            "Rendement par secteur et usine : matière versée, en-cours, semi-fini en silo, produit fini.",
            "Période, secteur", "Nouveau (cahier des charges 4.9)"),
        new NavItem("silos", "Silos", "Production", Modules.Silos, "module/silos", false,
            "Trois écrans silos : niveau, produit, conformité, dernière actualisation, mise à jour manuelle.",
            "Aucun (état instantané) ; l'historique a ses dates", "Nouveau (cahier des charges 4.3)"),
        new NavItem("dechets", "Déchets", "Production", Modules.Dechets, "module/dechets", false,
            "Déchet emballage par machine, équipe et poste ; déchet produit par secteur ; en kg et en % de la production.",
            "Période, secteur, machine, équipe, poste", "Nouveau, alimenté par les saisies"),

        new NavItem("humidite", "Humidité", "Qualité", Modules.Qualite, "module/humidite", false,
            "Humidité moyenne par produit et par ligne, tendance et seuils.",
            "Période, ligne, produit", "Nouveau (cahier des charges 4.6), base Qualité"),

        new NavItem("maintenance", "Indicateurs", "Maintenance", Modules.Maintenance, "module/maintenance", false,
            "Préventif réalisé, ordres de travail clôturés, arrêts dus à la maintenance, pannes répétitives, grandes réparations.",
            "Mois, machine", "Nouveau (cahier des charges 4.7), GMAO"),
        new NavItem("arrets", "Arrêts et planification", "Maintenance", Modules.Maintenance, "maintenance/arrets", true),
        new NavItem("couts", "Pièces et consommables", "Maintenance", Modules.Maintenance, "module/couts", false,
            "Coût et consommation des pièces de rechange et consommables par machine, cumul comparé à l'objectif.",
            "Mois, machine", "Nouveau (cahier des charges 4.7), GMAO"),

        new NavItem("energie", "Électricité et vapeur", "Énergie", Modules.Energie, "module/energie", false,
            "kWh et tonnes de vapeur par jour, ratios par tonne de produit fini, comparés aux objectifs.",
            "Période", "Nouveau (cahier des charges 4.8)"),

        new NavItem("absences", "Absentéisme", "Ressources humaines", Modules.Rh, "module/absences", false,
            "Taux d'absence : agrégé par équipe, ou nominatif avec le droit RH nominatif.",
            "Période, équipe", "Nouveau (cahier des charges 4.5), base RH"),

        new NavItem("saisie-emballage", "Déchets emballage", "Saisie", Modules.SaisieDechets, "saisie/dechets-emballage", true),
        new NavItem("saisie-produit", "Déchets produit", "Saisie", Modules.SaisieDechets, "saisie/dechets-produit", true),
        new NavItem("saisie-rendement", "Quantités rendement", "Saisie", Modules.SaisieQuantites, "module/saisie-rendement", false,
            "Quantités du jour par secteur pour le calcul du rendement.",
            "Contexte de saisie : date, secteur", "Nouveau (cahier des charges 4.9)"),
        new NavItem("saisie-energie", "Relevés énergie", "Saisie", Modules.SaisieQuantites, "module/saisie-energie", false,
            "Relevés manuels des compteurs quand la source n'est pas connectée.",
            "Contexte de saisie : date", "Nouveau (cahier des charges 4.8)"),

        new NavItem("acces", "Utilisateurs et rôles", "Administration", Modules.Utilisateurs, "admin/acces", true),
        new NavItem("objectifs", "Objectifs", "Administration", Modules.Objectifs, "module/objectifs", false,
            "Cibles TRS, consignes de déchets, objectifs de coûts et de consommations, avec date d'effet.",
            "Année, domaine", "Reprend les cibles TRS des Paramètres actuels"),
        new NavItem("referentiels", "Référentiels", "Administration", Modules.Referentiels, "module/referentiels", false,
            "Machines, lignes, secteurs, produits et cadences, équipes et roulement, pauses, silos, motifs de déchet.",
            "Recherche", "Reprend cadences, équipes et pauses des Paramètres actuels"),
        new NavItem("sources", "Sources de données", "Administration", Modules.Sources, "admin/sources", true),
        new NavItem("audit", "Journal d'audit", "Administration", Modules.Audit, "admin/audit", true),
        new NavItem("parametres", "Paramètres", "Administration", Modules.Parametres, "module/parametres", false,
            "Paramètres du poste : écran TV, rotation des diapositives, seuil d'arrêt, connexion à la base de production.",
            "Aucun", "Fenêtre Paramètres actuelle"),
    };

    public static NavItem? FindNav(string key) => Navigation.FirstOrDefault(n => n.Key == key);
}
