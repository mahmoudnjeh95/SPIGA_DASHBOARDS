namespace Spiga.Core.Security;

// Starting points offered at installation (and later, in Utilisateurs et
// rôles › Rôles › Ajouter des modèles). Optional: once created they are
// ordinary roles, renamed, changed or deleted like any other. Nothing in the
// application depends on them.
public static class RoleTemplates
{
    public const string AdminCode = "admin";

    public static Role Administrator() => new()
    {
        Code = AdminCode,
        Name = "Administrateur",
        Description = "Tous les droits. Rôle système, non modifiable.",
        IsSystem = true,
        Permissions = PermissionSet.Full(),
    };

    public sealed record Template(string Code, string Name, string Description, string[] Permissions, string[] Assignable);

    public static readonly IReadOnlyList<Template> All = WithOpening(new Template[]
    {
        new("direction", "Direction", "Consultation de toute l'usine et exports.",
            Views(Modules.VueEnsemble, Modules.Conditionnement, Modules.Rendement, Modules.Silos, Modules.Dechets, Modules.Qualite,
                  Modules.Maintenance, Modules.Energie, Modules.Rh, Modules.Objectifs, Modules.Audit)
            .Concat(Exports(Modules.VueEnsemble, Modules.Conditionnement, Modules.Rendement, Modules.Dechets, Modules.Qualite,
                            Modules.Maintenance, Modules.Energie, Modules.Rh))
            .Append(Perm.EquipesMultiples).ToArray(),
            Array.Empty<string>()),
        new("resp-production", "Responsable production", "Production, saisies et validation, objectifs ; gère les comptes chefs d'équipe et opérateurs.",
            Views(Modules.VueEnsemble, Modules.Conditionnement, Modules.Rendement, Modules.Silos, Modules.Dechets, Modules.Qualite,
                  Modules.Maintenance, Modules.Energie, Modules.Rh, Modules.Referentiels, Modules.Utilisateurs)
            .Concat(new[]
            {
                Perm.EquipesMultiples, "vue-ensemble:exporter", "conditionnement:exporter", Perm.TableauDetaille, Perm.EcranTv, Perm.NoteEcran, "rendement:exporter", "dechets:exporter",
                Perm.DechetsSaisir, Perm.DechetsProduit, Perm.DechetsAutresPostes, Perm.DechetsValider, Perm.DechetsCorriger, Perm.DechetsAutrui, Perm.DechetsTardif,
                "saisie-quantites:saisir", "saisie-quantites:valider", "objectifs:saisir", Perm.ComptesGerer, Perm.ComptesDebloquer,
                Perm.PointageVoir, Perm.PointageSaisir, Perm.PointageCorriger, Perm.PointageAnciens, Perm.HeuresSup, Perm.ReferentielsSaisir,
                Perm.View(Modules.RhNominatif), "rh-nominatif:exporter", "rh:exporter", Perm.Polyvalence,
            }).ToArray(),
            new[] { "chef-equipe", "operateur" }),
        new("chef-equipe", "Chef d'équipe", "Chef de conditionnement : suivi de son équipe, pointage, saisie, validation et renvoi des déchets.",
            Views(Modules.VueEnsemble, Modules.Conditionnement, Modules.Rendement, Modules.Silos, Modules.Dechets)
            .Concat(new[] { Perm.TableauDetaille, "conditionnement:exporter", Perm.DechetsSaisir, Perm.DechetsProduit, Perm.DechetsAutresPostes,
                Perm.DechetsValider, Perm.DechetsAutrui, "saisie-quantites:saisir", Perm.PointageVoir, Perm.PointageSaisir, Perm.PointageCorriger })
            .ToArray(),
            Array.Empty<string>()),
        new("operateur", "Opérateur de saisie", "Saisie du déchet emballage de ses machines, sur le poste en cours de son équipe ; ses statistiques.",
            new[] { Perm.View(Modules.Conditionnement), Perm.View(Modules.Dechets), Perm.DechetsSaisir },
            Array.Empty<string>()),
        new("resp-qualite", "Responsable qualité", "Qualité, conformité des silos.",
            Views(Modules.VueEnsemble, Modules.Qualite, Modules.Silos, Modules.Dechets, Modules.Rendement)
            .Concat(new[] { Perm.EquipesMultiples, "qualite:exporter", "silos:saisir", "silos:valider", "silos:exporter" }).ToArray(),
            Array.Empty<string>()),
        new("resp-maintenance", "Responsable maintenance", "Maintenance, arrêts et planification, objectifs maintenance.",
            Views(Modules.VueEnsemble, Modules.Conditionnement, Modules.Maintenance, Modules.Energie, Modules.Objectifs)
            .Concat(new[] { Perm.EquipesMultiples, Perm.TableauDetaille, "maintenance:saisir", "maintenance:valider", "maintenance:exporter", "objectifs:saisir" }).ToArray(),
            Array.Empty<string>()),
        new("resp-rh", "Responsable RH", "Absentéisme, y compris nominatif.",
            new[] { Perm.View(Modules.VueEnsemble), Perm.EquipesMultiples, Perm.View(Modules.Rh), "rh:exporter", Perm.View(Modules.RhNominatif), "rh-nominatif:exporter",
                Perm.Polyvalence, Perm.PointageVoir, Perm.View(Modules.Referentiels) },
            Array.Empty<string>()),
        // Screens signed in with an account of their own (web or desktop):
        // they open their one view directly, nothing else.
        new("ecran-dashboard", "Écran tableau de bord", "Ouvre SPIGA Dashboard seul : tableau de bord, historique et arrêts de toute l'usine.",
            new[] { Perm.View(Modules.Conditionnement), Perm.TableauDetaille, Perm.OuvrirDashboard, "conditionnement:exporter", Perm.View(Modules.Maintenance) },
            Array.Empty<string>()),
        new("ecran-tv", "Écran TV", "Ouvre SPIGA TV seul : les diapositives en plein écran (périmètre : toute l'usine).",
            new[] { Perm.View(Modules.Conditionnement), Perm.TableauDetaille, Perm.EcranTv },
            Array.Empty<string>()),
    });

    // Every template but the screens opens the application; those with the
    // detailed dashboard open SPIGA Dashboard too.
    private static Template[] WithOpening(Template[] templates)
        => templates.Select(t => t.Code.StartsWith("ecran-", StringComparison.Ordinal) ? t : t with
        {
            Permissions = t.Permissions.Append(Perm.OuvrirApplication)
                .Concat(t.Permissions.Contains(Perm.TableauDetaille) ? new[] { Perm.OuvrirDashboard } : Array.Empty<string>())
                .ToArray(),
        }).ToArray();

    private static IEnumerable<string> Views(params string[] modules) => modules.Select(Perm.View);

    private static IEnumerable<string> Exports(params string[] modules) => modules.Select(m => m + ":exporter");
}
