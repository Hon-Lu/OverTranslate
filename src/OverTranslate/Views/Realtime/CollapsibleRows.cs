namespace OverTranslate.Views.Realtime;

/// <summary>
/// Which rows of a list on the 即時翻譯 page show while it is folded: the first few, plus any row
/// that has to stay in view — and how many the 顯示其餘 line under it stands for.
/// </summary>
/// <remarks>
/// <para>The page scrolls; a list inside it that scrolled too took the wheel from the page whenever
/// the pointer crossed it, and the page stalled until the list reached its end. A long list folds
/// instead: a few rows and a line that unfolds the rest, and the card grows to hold them. Used by
/// the 翻譯區域預設 card and by the 模式 card's custom modes, so the two fold alike.</para>
///
/// <para>A row that has to stay in view — the preset a target opens on, a mode just made — is shown
/// in its own place in the order, after the first rows, rather than moved up: the order is the
/// user's, and a row that jumped to the top while folded would be somewhere else unfolded. It is
/// not counted in 其餘 N, which counts only what the line would add.</para>
/// </remarks>
internal static class CollapsibleRows
{
    /// <summary>How many rows a folded list shows before its 顯示其餘 line.</summary>
    public const int Folded = 3;

    /// <summary>
    /// The rows to show, as indexes in the list's order, and how many are left out.
    /// </summary>
    /// <param name="count">How many rows the list has.</param>
    /// <param name="keep">Rows that show even when folded; an index out of range is ignored.</param>
    /// <param name="expanded">Whether the user unfolded the list.</param>
    public static (IReadOnlyList<int> Shown, int Hidden) Shown(int count, IEnumerable<int>? keep, bool expanded)
    {
        if (count <= 0) return ([], 0);
        if (expanded || count <= Folded) return ([.. Enumerable.Range(0, count)], 0);

        var kept = new HashSet<int>((keep ?? []).Where(index => index >= Folded && index < count));
        var shown = Enumerable.Range(0, count).Where(index => index < Folded || kept.Contains(index)).ToList();
        return (shown, count - shown.Count);
    }

    /// <summary>Whether the list is long enough to have the 顯示其餘／收起 line at all.</summary>
    public static bool Folds(int count) => count > Folded;
}
