using Spiga.Core.Audit;
using Spiga.Core.Plant;
using Spiga.Core.Security;
using Spiga.Core.Waste;

namespace Spiga.Tests;

public class WasteTests
{
    private static readonly DateTime Today = new(2026, 11, 17);

    private static WasteDraft Emballage(string machine, double kg = 12.5, int shift = 1, DateTime? date = null)
        => new(WasteKind.Emballage, date ?? Today, shift, machine, null, kg, WasteUnits.Kg, "Réglage machine", "");

    [Fact]
    public async Task Operator_enters_waste_only_inside_their_scope()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur", TestApp.Sector(Sectors.PateCourte));

        var entry = await app.WasteService.AddAsync(op, Emballage("PC01"));
        Assert.Equal(Sectors.PateCourte, entry.Sector);
        Assert.Equal("Equipe B", entry.Team);
        Assert.Equal(WasteStatus.Brouillon, entry.Status);

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.AddAsync(op, Emballage("CC01")));
        Assert.Equal(new[] { "PC01", "PC02" }, (await app.WasteService.MachinesFor(op)).Select(m => m.Id));
    }

    [Fact]
    public async Task Chef_of_one_team_cannot_enter_for_a_poste_worked_by_another_team()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe", TestApp.Team("Equipe B"));
        await app.WasteService.AddAsync(chef, Emballage("PC01", shift: 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.AddAsync(chef, Emballage("PC01", shift: 3)));
    }

    [Fact]
    public async Task Entry_is_validated_by_someone_else_then_locked()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        var entry = await app.WasteService.AddAsync(op, Emballage("PC01"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.ValidateAsync(op, entry.Id, 1));

        var ownEntry = await app.WasteService.AddAsync(chef, Emballage("PC02"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ValidateAsync(chef, ownEntry.Id, 1));

        await app.WasteService.ValidateAsync(chef, entry.Id, 1);
        var validated = await app.Waste.GetAsync(entry.Id);
        Assert.Equal(WasteStatus.Validee, validated!.Status);
        Assert.Equal("chef.b", validated.ValidatedBy);

        // Locked: no deletion; correction only with the "corriger" right and a reason.
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.UpdateAsync(op, entry.Id, 2, Emballage("PC01", 20), null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.UpdateAsync(chef, entry.Id, 2, Emballage("PC01", 20), "Pesée refaite"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.DeleteAsync(chef, entry.Id, 2));
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.UpdateAsync(prod, entry.Id, 2, Emballage("PC01", 20), " "));
        await app.WasteService.UpdateAsync(prod, entry.Id, 2, Emballage("PC01", 20), "Pesée refaite");

        var correction = Assert.Single(await app.AuditAsync(), e => e.Action == AuditActions.EntryCorrected);
        Assert.Contains("12,5 kg", correction.OldValue);
        Assert.Contains("20 kg", correction.NewValue);
        Assert.Equal("Pesée refaite", correction.Details);
    }

    [Fact]
    public async Task Draft_can_be_changed_or_deleted_by_its_author_only()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op1 = await app.UserAsync(admin, "operateur.a", "operateur");
        var op2 = await app.UserAsync(admin, "operateur.b", "operateur");
        var entry = await app.WasteService.AddAsync(op1, Emballage("PC01"));

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.DeleteAsync(op2, entry.Id, 1));
        await app.WasteService.UpdateAsync(op1, entry.Id, 1, Emballage("PC01", 14), null);
        await app.WasteService.DeleteAsync(op1, entry.Id, 2);
        Assert.Empty(await app.WasteService.ListAsync(admin, WasteKind.Emballage, Today, null));
    }

    [Fact]
    public async Task Entries_are_checked_before_they_are_saved()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");

        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(op, Emballage("PC01", kg: 0)));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(op, Emballage("PC01", date: Today.AddDays(1))));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(op, Emballage("PC01", date: Today.AddDays(-8))));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(op,
            new WasteDraft(WasteKind.Emballage, Today, 1, "PC01", null, 5, WasteUnits.Kg, "Motif libre", "")));

        // Late entries need their own right.
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(chef, Emballage("PC01", date: Today.AddDays(-8))));
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        await app.WasteService.AddAsync(prod, Emballage("PC01", date: Today.AddDays(-8)));
    }

    [Fact]
    public async Task Product_waste_is_entered_per_sector_and_converted_to_kg()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var entry = await app.WasteService.AddAsync(admin,
            new WasteDraft(WasteKind.Produit, Today, 2, null, Sectors.Couscous, 1.2, WasteUnits.Tonne, "Démarrage de ligne", "Redémarrage après panne"));
        Assert.Null(entry.MachineId);
        Assert.Equal(1200, entry.QuantityKg);
        var listed = Assert.Single(await app.WasteService.ListAsync(admin, WasteKind.Produit, Today, 2));
        Assert.Equal("Redémarrage après panne", listed.Comment);
    }

    [Fact]
    public async Task Lists_hide_entries_outside_the_scope()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        await app.WasteService.AddAsync(admin, Emballage("PC01"));
        await app.WasteService.AddAsync(admin, Emballage("CC01"));
        var op = await app.UserAsync(admin, "operateur.cc", "operateur", TestApp.Sector(Sectors.Couscous));
        // The operator template cannot consult entries: give the right, then list.
        var role = await app.RoleAsync("operateur");
        await app.Admin.UpdateRoleAsync(admin, role.Id, new RoleChange(role.Name, role.Description, true,
            role.Permissions.With(Perm.View(Modules.SaisieDechets)), role.AssignableRoleIds));
        op = (await app.Auth.RefreshAsync(op))!;
        var visible = await app.WasteService.ListAsync(op, WasteKind.Emballage, Today, null);
        Assert.Equal("CC01", Assert.Single(visible).MachineId);
    }

    private static WasteDraft Produit(string sector, double kg = 30, int shift = 1)
        => new(WasteKind.Produit, Today, shift, null, sector, kg, WasteUnits.Kg, "Démarrage de ligne", "");

    [Fact]
    public async Task A_validator_validates_only_the_version_they_saw()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        var entry = await app.WasteService.AddAsync(op, Emballage("PC01", 12));
        Assert.Equal(1, entry.Version);

        // The chef's screen shows v1 (12 kg); meanwhile the operator changes it to 40 kg.
        await app.WasteService.UpdateAsync(op, entry.Id, 1, Emballage("PC01", 40), null);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ValidateAsync(chef, entry.Id, 1));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.UpdateAsync(op, entry.Id, 1, Emballage("PC01", 41), null));
        Assert.Equal(WasteStatus.Brouillon, (await app.Waste.GetAsync(entry.Id))!.Status);

        await app.WasteService.ValidateAsync(chef, entry.Id, 2);
        var validated = (await app.Waste.GetAsync(entry.Id))!;
        Assert.Equal(40, validated.Quantity);
        Assert.Equal(3, validated.Version);
    }

    [Fact]
    public async Task Returned_entry_is_corrected_and_resubmitted_with_its_history()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        var entry = await app.WasteService.AddAsync(op, Emballage("PC01", 12));

        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ReturnAsync(chef, entry.Id, 1, "  "));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.ReturnAsync(op, entry.Id, 1, "?"));
        await app.WasteService.ReturnAsync(chef, entry.Id, 1, "Repeser le carton");

        var returned = Assert.Single(await app.WasteService.ReturnedToAsync(op));
        Assert.Equal(WasteStatus.Renvoyee, returned.Status);
        Assert.Equal("Repeser le carton", returned.ReturnComment);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ValidateAsync(chef, entry.Id, 2));

        // A returned entry of an earlier poste can still be corrected by an
        // operator who only enters for the current poste.
        app.Clock.Now = Today.AddDays(1).AddHours(7);
        await app.WasteService.UpdateAsync(op, entry.Id, 2, Emballage("PC01", 9.5), null);
        var resubmitted = (await app.Waste.GetAsync(entry.Id))!;
        Assert.Equal(WasteStatus.Brouillon, resubmitted.Status);
        Assert.Empty(await app.WasteService.ReturnedToAsync(op));

        await app.WasteService.ValidateAsync(chef, entry.Id, 3);
        var history = await app.WasteService.HistoryAsync(op, entry.Id);
        Assert.Equal(new[] { AuditActions.EntryCreated, AuditActions.EntryReturned, AuditActions.EntryResubmitted, AuditActions.EntryValidated },
            history.Select(r => r.Action));
        Assert.Equal(new[] { 1, 2, 3, 4 }, history.Select(r => r.Version));
        Assert.Contains("12 kg", history[0].Snapshot);
        Assert.Contains("9,5 kg", history[2].Snapshot);
        Assert.Equal("Repeser le carton", history[1].Note);
    }

    [Fact]
    public async Task Whoever_changed_a_draft_cannot_validate_it()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        var entry = await app.WasteService.AddAsync(op, Emballage("PC01", 12));

        await app.WasteService.UpdateAsync(chef, entry.Id, 1, Emballage("PC01", 15), null);
        var changed = (await app.Waste.GetAsync(entry.Id))!;
        Assert.False(app.WasteService.CanValidate(chef, changed, null));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ValidateAsync(chef, entry.Id, 2));
        await app.WasteService.ValidateAsync(prod, entry.Id, 2);
    }

    [Fact]
    public async Task Validated_entry_is_cancelled_with_a_reason_and_counts_nowhere()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var op = await app.UserAsync(admin, "operateur.pc", "operateur");
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        var prod = await app.UserAsync(admin, "resp.prod", "resp-production");
        var first = await app.WasteService.AddAsync(op, Emballage("PC01", 12));
        var twice = await app.WasteService.AddAsync(op, Emballage("PC01", 12));
        await app.WasteService.ValidateAsync(chef, first.Id, 1);
        await app.WasteService.ValidateAsync(chef, twice.Id, 1);

        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.CancelAsync(prod, first.Id + 1000, 1, "x"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.CancelAsync(chef, twice.Id, 2, "Saisie en double"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.CancelAsync(prod, twice.Id, 2, " "));
        await app.WasteService.CancelAsync(prod, twice.Id, 2, "Saisie en double");

        var cancelled = (await app.Waste.GetAsync(twice.Id))!;
        Assert.Equal(WasteStatus.Annulee, cancelled.Status);
        Assert.Equal("Saisie en double", cancelled.CancelReason);
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.UpdateAsync(prod, twice.Id, 3, Emballage("PC01", 1), "x"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.DeleteAsync(prod, twice.Id, 3));

        // Still listed (struck through), no longer counted.
        Assert.Equal(2, (await app.WasteService.ListAsync(prod, WasteKind.Emballage, Today, 1)).Count);
        Assert.Equal(12, (await app.WasteService.ForDashboardAsync(prod, Today, Today)).Sum(e => e.QuantityKg));
    }

    [Fact]
    public async Task Operator_enters_packaging_waste_of_their_machine_on_their_poste_only()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        var cc = new DataScope(new[] { Sectors.Couscous }, null, new[] { "CC01" });
        var op = await app.UserAsync(admin, "operateur.cc", "operateur", cc);

        Assert.Equal(new[] { "CC01" }, (await app.WasteService.MachinesFor(op)).Select(m => m.Id));
        Assert.False(app.WasteService.CanChoosePoste(op));
        Assert.Equal((Today, 1), app.WasteService.CurrentPoste(op));
        Assert.True(op.Can(Perm.View(Modules.Dechets)));

        await app.WasteService.AddAsync(op, Emballage("CC01"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.AddAsync(op, Emballage("PC01")));
        // Yesterday's morning was also Equipe B's poste, but it is not the current one.
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(op, Emballage("CC01", date: Today.AddDays(-1))));
        // No product waste: not their right, nor their whole sector.
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.AddAsync(op, Produit(Sectors.Couscous)));
        Assert.Empty(app.WasteService.SectorsFor(op));

        // At 14:30 the afternoon belongs to Equipe A: the operator of Equipe B
        // still enters for the morning that just ended.
        app.Clock.Now = Today.AddHours(14.5);
        Assert.Equal((Today, 1), app.WasteService.CurrentPoste(op));
        await app.WasteService.AddAsync(op, Emballage("CC01", 3));
    }

    [Fact]
    public async Task Product_waste_needs_the_whole_sector()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        await app.WasteService.AddAsync(admin, Produit(Sectors.Couscous));
        var machineChef = await app.UserAsync(admin, "chef.cc01", "chef-equipe",
            new DataScope(new[] { Sectors.Couscous }, null, new[] { "CC01" }));
        var sectorChef = await app.UserAsync(admin, "chef.cc", "chef-equipe", TestApp.Sector(Sectors.Couscous));

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.AddAsync(machineChef, Produit(Sectors.Couscous)));
        Assert.Empty(await app.WasteService.ListAsync(machineChef, WasteKind.Produit, Today, null));
        Assert.Single(await app.WasteService.ListAsync(sectorChef, WasteKind.Produit, Today, null));
        Assert.Equal(new[] { Sectors.Couscous }, app.WasteService.SectorsFor(sectorChef));
        await app.WasteService.AddAsync(sectorChef, Produit(Sectors.Couscous));
    }

    [Fact]
    public async Task Service_refuses_what_the_form_would_have_refused()
    {
        using var app = new TestApp();
        var admin = await app.SetupAdminAsync();
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(admin,
            new WasteDraft(WasteKind.Emballage, Today, 1, "PC01", null, 1.2345, WasteUnits.Kg, "Réglage machine", "")));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.AddAsync(admin,
            new WasteDraft(WasteKind.Emballage, Today, 1, "PC01", null, 4, WasteUnits.Kg, "Autre", "  ")));
        await app.WasteService.AddAsync(admin,
            new WasteDraft(WasteKind.Emballage, Today, 1, "PC01", null, 4, WasteUnits.Kg, "Autre", "palette tombée"));
    }
}
