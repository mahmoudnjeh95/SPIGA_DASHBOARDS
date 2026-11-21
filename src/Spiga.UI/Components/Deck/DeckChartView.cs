using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Spiga.UI.Services;

namespace Spiga.UI.Components.Deck;

// A deck chart in its card: the SpigaChart, or "Pas de données" under the title.
public static class DeckChartView
{
    public static RenderFragment Render(string title, string key, DeckChart chart, int height = 230) => builder =>
    {
        if (!chart.HasData)
        {
            builder.OpenElement(0, "h2");
            builder.AddContent(1, title);
            builder.CloseElement();
            builder.OpenElement(2, "p");
            builder.AddAttribute(3, "class", "empty");
            builder.AddContent(4, "Pas de données.");
            builder.CloseElement();
            return;
        }
        builder.OpenComponent<SpigaChart>(5);
        builder.AddAttribute(6, nameof(SpigaChart.Title), title);
        builder.AddAttribute(7, nameof(SpigaChart.Key), "deck-" + key);
        builder.AddAttribute(8, nameof(SpigaChart.Labels), chart.Labels);
        builder.AddAttribute(9, nameof(SpigaChart.Series), chart.Series);
        builder.AddAttribute(10, nameof(SpigaChart.Format), chart.Format);
        builder.AddAttribute(11, nameof(SpigaChart.YMin), chart.YMin);
        builder.AddAttribute(12, nameof(SpigaChart.YMax), chart.YMax);
        builder.AddAttribute(13, nameof(SpigaChart.Height), height);
        if (chart.Times != null)
            builder.AddAttribute(14, nameof(SpigaChart.Times), chart.Times);
        builder.CloseComponent();
    };
}
