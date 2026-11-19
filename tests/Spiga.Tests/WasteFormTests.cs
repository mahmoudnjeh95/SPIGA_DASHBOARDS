using Spiga.Core.Waste;
using Spiga.UI.Services;

namespace Spiga.Tests;

public class WasteFormTests
{
    private static WasteFormCheck Check(string qty, string unit = WasteUnits.Kg, string place = "CC01", string reason = "Réglage machine",
        string comment = "", bool correction = false, string fix = "")
        => WasteFormCheck.Of(new WasteFormInput(WasteKind.Emballage, place, qty, unit, reason, comment, correction, fix));

    [Theory]
    [InlineData("12,5", 12.5)]
    [InlineData("12.5", 12.5)]
    [InlineData(" 1 250 ", 1250)]
    [InlineData("0,125", 0.125)]
    [InlineData(",5", 0.5)]
    public void Typed_numbers_are_read_the_French_way(string typed, double expected)
    {
        var (value, error) = WasteFormCheck.ParseQuantity(typed);
        Assert.Null(error);
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("12kg")]
    [InlineData("1e3")]
    [InlineData("-4")]
    [InlineData("12,5,3")]
    [InlineData("1.2,5")]
    [InlineData("0,1255")]
    [InlineData("0")]
    [InlineData("0,000")]
    [InlineData("1234567")]
    [InlineData(",")]
    public void Typos_are_refused_while_typing(string typed)
    {
        Assert.NotNull(WasteFormCheck.ParseQuantity(typed).Error);
        Assert.False(Check(typed).Complete);
    }

    [Fact]
    public void Missing_parts_are_named_and_block_the_form()
    {
        var empty = WasteFormCheck.Of(new WasteFormInput(WasteKind.Emballage, "", "", WasteUnits.Kg, "", ""));
        Assert.Equal(new[] { "la machine", "la quantité", "le motif" }, empty.Missing);
        Assert.False(empty.Complete);

        Assert.Equal(new[] { "le secteur" },
            WasteFormCheck.Of(new WasteFormInput(WasteKind.Produit, null, "3", WasteUnits.Kg, "Démarrage de ligne", "")).Missing);
        Assert.Contains("un commentaire (motif « Autre »)", Check("3", reason: "Autre").Missing);
        Assert.True(Check("3", reason: "Autre", comment: "palette tombée").Complete);
        Assert.Contains("le motif de la correction", Check("3", correction: true).Missing);
        Assert.True(Check("3", correction: true, fix: "pesée refaite").Complete);
    }

    [Fact]
    public void Unusual_or_impossible_quantities_are_flagged()
    {
        Assert.False(Check("150").Unusual);
        Assert.True(Check("1250").Unusual);
        Assert.True(Check("1,5", WasteUnits.Tonne).Unusual);
        Assert.Equal(1500, Check("1,5", WasteUnits.Tonne).QuantityKg);
        Assert.NotNull(Check("150", WasteUnits.Tonne).QuantityError);
    }

    [Fact]
    public void Periods_are_production_days_from_6h_to_6h()
    {
        // 00:55 on the 19th: the night poste of the 18th is in progress.
        var night = new DateTime(2026, 11, 19, 0, 55, 0);
        var today = Period.Today(night);
        Assert.Equal((new DateTime(2026, 11, 18), new DateTime(2026, 11, 18)), today.Days(night));
        Assert.Equal((new DateTime(2026, 11, 18, 6, 0, 0), new DateTime(2026, 11, 19, 6, 0, 0)), today.Range(night));
        Assert.True(today.IncludesNow(night));
        Assert.Contains("poste de nuit en cours", today.Label(night));

        var morning = new DateTime(2026, 11, 19, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 19, 6, 0, 0), Period.Today(morning).Range(morning).From);
        var week = new Period(PeriodKind.Last7Days, morning, morning);
        Assert.Equal((new DateTime(2026, 11, 13, 6, 0, 0), new DateTime(2026, 11, 20, 6, 0, 0)), week.Range(morning));
        Assert.Equal((new DateTime(2026, 11, 13), new DateTime(2026, 11, 20)), week.CalendarRange(morning));
    }
}
