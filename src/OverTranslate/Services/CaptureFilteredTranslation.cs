using OverTranslate.Services.Realtime;

namespace OverTranslate.Services;

/// <summary>
/// One capture's translation under 過濾數字與縮寫: which groups are asked for, which are drawn,
/// and what each one holds — kept for as long as the capture is up, so the switch in the toolbar's
/// 設定 menu can be turned either way over the capture already on screen.
/// </summary>
/// <remarks>
/// <para>The decision is realtime translation's, unchanged: a group is left out when the switch is
/// on and <see cref="RealtimeHiddenText.NothingToTranslate"/> says there is nothing in its text to
/// translate. Judged group by group, from the text alone, after grouping, as realtime judges it.</para>
///
/// <para>Left out, not dropped. A group left out is still one of <see cref="Blocks"/>, at its own
/// index, holding its original text and marked <see cref="TranslatedBlock.Hidden"/>: everything but
/// the drawing still needs it. The bubbles beside it are laid out against its box as they were
/// before, so they keep to their own room rather than growing over the number it is, and turning
/// the switch moves nothing but the bubbles it hides or shows. Copying the translation reads its
/// original text where a translation would be, so the number is still in what is pasted.</para>
///
/// <para>Turned off, only the groups that were never asked for are asked for — see
/// <see cref="Ask"/>. A group that already has an answer, from before the switch was turned on, is
/// shown again with it; nothing else is translated a second time.</para>
///
/// <para>Off from the start, every group is asked for at once and <see cref="Blocks"/> hands the
/// answer back entry for entry, the same instances the engine returned: a capture with the switch
/// off is translated and drawn exactly as it was before the switch existed.</para>
/// </remarks>
internal sealed class CaptureFilteredTranslation
{
    private readonly IReadOnlyList<OcrTextBlock> _source;
    private readonly bool[] _nothingToTranslate;
    private readonly TranslatedBlock?[] _answers;
    private readonly bool[] _asking;

    public CaptureFilteredTranslation(IReadOnlyList<OcrTextBlock> source, bool filter)
    {
        _source = source;
        _nothingToTranslate = [.. source.Select(block => RealtimeHiddenText.NothingToTranslate(block.Text))];
        _answers = new TranslatedBlock?[source.Count];
        _asking = new bool[source.Count];
        Filter = filter;
    }

    /// <summary>Whether 過濾數字與縮寫 is on for this capture.</summary>
    public bool Filter { get; private set; }

    /// <summary>The groups as recognised, which every index here refers to.</summary>
    public IReadOnlyList<OcrTextBlock> Source => _source;

    /// <summary>
    /// Whether a group is neither asked for nor drawn: the switch on, and nothing in it to translate.
    /// The one test both the request and the drawing go by.
    /// </summary>
    public static bool LeftOut(OcrTextBlock block, bool filter) =>
        filter && RealtimeHiddenText.NothingToTranslate(block.Text);

    public void SetFilter(bool filter) => Filter = filter;

    /// <summary>Whether group <paramref name="index"/> is left out under the switch as it is now.</summary>
    public bool IsLeftOut(int index) => Filter && _nothingToTranslate[index];

    /// <summary>Whether <see cref="TranslateAsync"/> would ask the engine for anything now.</summary>
    public bool NeedsAsking =>
        Enumerable.Range(0, _source.Count).Any(i => !IsLeftOut(i) && _answers[i] is null && !_asking[i]);

    /// <summary>
    /// Asks <paramref name="engine"/> for every group that needs asking — see <see cref="Ask"/> —
    /// and takes its answer. Nothing is asked when nothing needs to be.
    /// </summary>
    /// <param name="engine">
    /// The translation: handed the groups in the order read, it answers one block per group.
    /// </param>
    public async Task<CaptureFilterAnswer> TranslateAsync(
        Func<List<OcrTextBlock>, Task<List<TranslatedBlock>>> engine)
    {
        var asked = Ask();
        if (asked.Count == 0) return new(asked, [], Taken: true);

        try
        {
            var answer = await engine(Request(asked));
            return new(asked, answer, Take(asked, answer));
        }
        catch
        {
            Release(asked);
            throw;
        }
    }

    /// <summary>
    /// The groups to ask the engine for now: not left out, never answered, and not already being
    /// asked for. Marks them as being asked for until <see cref="Take"/> or <see cref="Release"/>.
    /// </summary>
    public IReadOnlyList<int> Ask()
    {
        var asked = new List<int>();
        for (var i = 0; i < _source.Count; i++)
        {
            if (IsLeftOut(i) || _answers[i] is not null || _asking[i]) continue;
            _asking[i] = true;
            asked.Add(i);
        }
        return asked;
    }

    /// <summary>The groups <paramref name="asked"/> names, in order, as the engine is handed them.</summary>
    /// <remarks>The source list itself when that is every group, as a capture without the switch asks.</remarks>
    public List<OcrTextBlock> Request(IReadOnlyList<int> asked) =>
        asked.Count == _source.Count && _source is List<OcrTextBlock> all
            ? all
            : [.. asked.Select(i => _source[i])];

    /// <summary>
    /// Takes the engine's answer to <see cref="Ask"/>, one block per group asked for. False, with
    /// nothing taken, when it does not line up with what was asked.
    /// </summary>
    public bool Take(IReadOnlyList<int> asked, IReadOnlyList<TranslatedBlock> answer)
    {
        Release(asked);
        if (answer.Count != asked.Count) return false;
        for (var k = 0; k < asked.Count; k++)
            _answers[asked[k]] = answer[k];
        return true;
    }

    /// <summary>Gives up on groups that were being asked for, so the next <see cref="Ask"/> asks again.</summary>
    public void Release(IReadOnlyList<int> asked)
    {
        foreach (var i in asked) _asking[i] = false;
    }

    /// <summary>
    /// One block per group, in the order recognised: the engine's answer for a group that is drawn;
    /// for one that is not — left out, or still waiting for its answer — its own text, marked
    /// <see cref="TranslatedBlock.Hidden"/>.
    /// </summary>
    public List<TranslatedBlock> Blocks()
    {
        var blocks = new List<TranslatedBlock>(_source.Count);
        for (var i = 0; i < _source.Count; i++)
            blocks.Add(!IsLeftOut(i) && _answers[i] is { } answer ? answer : Unshown(_source[i]));
        return blocks;
    }

    /// <summary>A group standing in its own text, built the way the providers build theirs.</summary>
    private static TranslatedBlock Unshown(OcrTextBlock block) =>
        new(block.Text, block.Text, block.Bounds, block.Lines, block.RenderGlyphHeight)
        {
            RunsAcross = block.RunsAcross,
            FontGlyphHeight = block.FontGlyphHeight,
            Tilt = block.Tilt,
            Hidden = true,
        };
}

/// <summary>What one <see cref="CaptureFilteredTranslation.TranslateAsync"/> asked for and got back.</summary>
/// <param name="Asked">The groups asked for, by index; none when nothing needed asking.</param>
/// <param name="Answer">The engine's answer as it came.</param>
/// <param name="Taken">
/// Whether the answer lined up with what was asked and is now in
/// <see cref="CaptureFilteredTranslation.Blocks"/>; true when nothing was asked.
/// </param>
internal sealed record CaptureFilterAnswer(IReadOnlyList<int> Asked, List<TranslatedBlock> Answer, bool Taken);
