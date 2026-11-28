using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OverTranslate.Services;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace OverTranslate.Views.Realtime;

/// <summary>
/// Which rows of the 模式 card's custom list show while it is folded — always the same few — and
/// how many the 顯示其餘 row under them stands for.
/// </summary>
/// <remarks>
/// <para>The page scrolls; a list inside it that scrolled too took the wheel from the page whenever
/// the pointer crossed it, and the page stalled until the list reached its end. A long list folds
/// instead: a few rows and a row that unfolds the rest, and the card grows to hold them.</para>
///
/// <para>A folded list is always <see cref="Folded"/> rows, never one more: a row that has to stay
/// in view — the mode just made — is moved to the top instead of being added after the third, and
/// keeps that place unfolded, so unfolding only adds rows under the ones already read.</para>
/// </remarks>
internal static class CollapsibleRows
{
    /// <summary>How many rows a folded list shows before its 顯示其餘 row.</summary>
    public const int Folded = 3;

    /// <summary>
    /// The rows to show, as indexes into the list, in the order to show them, and how many are left out.
    /// </summary>
    /// <param name="count">How many rows the list has.</param>
    /// <param name="pinned">A row shown first, folded or not; null or out of range for none.</param>
    /// <param name="expanded">Whether the user unfolded the list.</param>
    public static (IReadOnlyList<int> Shown, int Hidden) Shown(int count, int? pinned, bool expanded)
    {
        if (count <= 0) return ([], 0);

        IEnumerable<int> order = Enumerable.Range(0, count);
        if (pinned is { } first && first >= 0 && first < count)
            order = order.Where(index => index != first).Prepend(first);

        var shown = (expanded ? order : order.Take(Folded)).ToList();
        return (shown, count - shown.Count);
    }

    /// <summary>Whether the list is long enough to have the 顯示其餘／收起 line at all.</summary>
    public static bool Folds(int count) => count > Folded;

    /// <summary>
    /// Whether the 顯示其餘／收起 row is there: the list folds, and either rows are folded away or
    /// it is unfolded.
    /// </summary>
    public static bool HasMoreRow(int count, int hidden, bool expanded) =>
        Folds(count) && (expanded || hidden > 0);

    /// <summary>
    /// The 顯示其餘 N 個／組 or 收起 row a folded list ends with, inside its frame (CardListMoreRow).
    /// </summary>
    /// <param name="moreKey">The string that names what is folded away, with {0} for how many.</param>
    public static Button MoreRow(int hidden, bool expanded, string moreKey, string automationId, Action toggle)
    {
        var text = expanded
            ? LocalizationService.Get("S.Realtime.ShowFewer")
            : LocalizationService.Format(moreKey, hidden);

        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(new TextBlock
        {
            Text = expanded ? "\uE70E" : "\uE70D",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 1, 0, 0),
        });

        var row = new Button { Content = line };
        row.SetResourceReference(FrameworkElement.StyleProperty, "CardListMoreRow");
        AutomationProperties.SetName(row, text);
        AutomationProperties.SetAutomationId(row, automationId);
        row.Click += (_, _) => toggle();
        return row;
    }
}
