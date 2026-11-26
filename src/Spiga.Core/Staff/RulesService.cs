using System.Text.Json;
using System.Text.Json.Serialization;
using Spiga.Core.Audit;
using Spiga.Core.Security;

namespace Spiga.Core.Staff;

// The rules of « À traiter » and of the permutation, kept in the settings;
// changed with the right « Paramétrer À traiter et la permutation », each
// change written to the audit log (before / after).
public sealed class RulesService(ISettingsStore settings, IAuditLog audit, TimeProvider clock, ClientInfo client)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    public bool CanManage(Actor actor) => actor.Can(Perm.Regles);

    public async Task<AttentionRules> AttentionAsync()
        => await settings.GetAsync<AttentionRules>(SettingKeys.AttentionRules) ?? new AttentionRules();

    public async Task<PermutationRule> PermutationAsync()
        => await settings.GetAsync<PermutationRule>(SettingKeys.PermutationRule) ?? new PermutationRule();

    public async Task SetAttentionAsync(Actor actor, AttentionRules rules)
    {
        actor.Demand(Perm.Regles);
        if (rules.Problem() is { } problem)
            throw new RuleViolationException(problem);
        rules.Personnel.AbsenceCodes = rules.Personnel.AbsenceCodes.Distinct().Order().ToList();
        var before = await AttentionAsync();
        var (was, now) = (JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(rules, Json));
        if (was == now)
            return;
        await settings.SetAsync(SettingKeys.AttentionRules, rules, actor.Login);
        await WriteAsync(actor, AuditActions.AttentionRulesChanged, "Règles « À traiter »", was, now);
    }

    public async Task SetPermutationAsync(Actor actor, PermutationRule rule)
    {
        actor.Demand(Perm.Regles);
        var before = await PermutationAsync();
        if (before.SameFunction == rule.SameFunction)
            return;
        await settings.SetAsync(SettingKeys.PermutationRule, rule, actor.Login);
        await WriteAsync(actor, AuditActions.PermutationRuleChanged, "Règle de permutation",
            Describe(before), Describe(rule));
    }

    private static string Describe(PermutationRule r) => r.SameFunction ? "même fonction obligatoire" : "toutes fonctions";

    private Task WriteAsync(Actor actor, string action, string target, string was, string now)
        => audit.WriteAsync(new AuditEntry
        {
            At = clock.GetLocalNow().DateTime, UserLogin = actor.Login, Module = Modules.VueEnsemble, Action = action,
            Target = target, OldValue = was, NewValue = now, Workstation = client.Workstation,
        });
}
