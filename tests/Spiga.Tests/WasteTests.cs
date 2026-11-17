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
        var op = await app.UserAsync(admin, "operateur.pc", "operateur",
            new DataScope(new[] { Sectors.PateCourte }, null, Array.Empty<string>()));

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
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe", new DataScope(Array.Empty<string>(), "Equipe B", Array.Empty<string>()));
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

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.ValidateAsync(op, entry.Id));

        var ownEntry = await app.WasteService.AddAsync(chef, Emballage("PC02"));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.ValidateAsync(chef, ownEntry.Id));

        await app.WasteService.ValidateAsync(chef, entry.Id);
        var validated = await app.Waste.GetAsync(entry.Id);
        Assert.Equal(WasteStatus.Validee, validated!.Status);
        Assert.Equal("chef.b", validated.ValidatedBy);

        // Locked for its author; no deletion; correction only with a reason.
        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.UpdateAsync(op, entry.Id, Emballage("PC01", 20), null));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.DeleteAsync(chef, entry.Id));
        await Assert.ThrowsAsync<RuleViolationException>(() => app.WasteService.UpdateAsync(chef, entry.Id, Emballage("PC01", 20), " "));
        await app.WasteService.UpdateAsync(chef, entry.Id, Emballage("PC01", 20), "Pesée refaite");

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

        await Assert.ThrowsAsync<AccessDeniedException>(() => app.WasteService.DeleteAsync(op2, entry.Id));
        await app.WasteService.UpdateAsync(op1, entry.Id, Emballage("PC01", 14), null);
        await app.WasteService.DeleteAsync(op1, entry.Id);
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

        // A validator may enter late.
        var chef = await app.UserAsync(admin, "chef.b", "chef-equipe");
        await app.WasteService.AddAsync(chef, Emballage("PC01", date: Today.AddDays(-8)));
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
        var op = await app.UserAsync(admin, "operateur.cc", "operateur",
            new DataScope(new[] { Sectors.Couscous }, null, Array.Empty<string>()));
        var visible = await app.WasteService.ListAsync(op, WasteKind.Emballage, Today, null);
        Assert.Equal("CC01", Assert.Single(visible).MachineId);
    }
}
