using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Components;

namespace Spiga.UI.Components;

// One column of a GridTable. Text: what the filter searches and what is
// shown without Cell. Sort: the value to sort on (a number, a date...),
// the text when not given.
public sealed class GridColumn<T>
{
    public GridColumn(string title, Func<T, string> text, Func<T, object?>? sort = null, RenderFragment<T>? cell = null,
        string css = "", bool filter = true, bool sortable = true)
    {
        Title = title;
        Text = text;
        SortValue = sort;
        Cell = cell;
        Css = css;
        Filter = filter;
        Sortable = sortable;
    }

    public string Title { get; }
    public Func<T, string> Text { get; }
    public Func<T, object?>? SortValue { get; }
    public RenderFragment<T>? Cell { get; }
    public string Css { get; }
    public bool Filter { get; }
    public bool Sortable { get; }

    public object? Key(T item) => SortValue != null ? SortValue(item) : Text(item);

    // Nulls last; numbers and dates by value, text in French order.
    public static readonly IComparer<object?> Comparer = Comparer<object?>.Create((a, b) =>
    {
        if (a == null) return b == null ? 0 : 1;
        if (b == null) return -1;
        if (a is string sa && b is string sb)
            return string.Compare(sa, sb, CultureInfo.GetCultureInfo("fr-FR"), CompareOptions.IgnoreCase);
        if (a is IComparable ca && a.GetType() == b.GetType())
            return ca.CompareTo(b);
        if (IsNumber(a) && IsNumber(b))
            return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b));
        return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
    });

    private static bool IsNumber(object o) => o is int or long or double or float or decimal;

    // Lower case without accents: "Équipe" matches "equipe".
    public static string Fold(string? text)
    {
        var d = (text ?? "").ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Trim();
    }
}
