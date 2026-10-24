using System.Windows.Media;
using OverTranslate.Services;

namespace OverTranslate.Views.Capture;

public partial class ToolbarWindow
{
    /// <summary>One of the two ways the text in the selection can be written, as the picker lists it.</summary>
    /// <remarks>
    /// Keys rather than text, resolved when read, for the reason <see cref="Models.ProviderItem"/>
    /// gives. Public because the picker's template binds to it.
    /// </remarks>
    public sealed class DirectionOption
    {
        public static readonly DirectionOption Horizontal = new(
            "S.Toolbar.DirectionHorizontal", "S.Toolbar.DirectionHorizontalHint", "M2,3.5 H12 M2,7 H12 M2,10.5 H8");

        public static readonly DirectionOption Vertical = new(
            "S.Toolbar.DirectionVertical", "S.Toolbar.DirectionVerticalHint", "M10.5,2 V12 M7,2 V12 M3.5,2 V8");

        public static readonly IReadOnlyList<DirectionOption> All = new[] { Horizontal, Vertical };

        private readonly string _nameKey;
        private readonly string _hintKey;

        private DirectionOption(string nameKey, string hintKey, string glyph)
        {
            _nameKey = nameKey;
            _hintKey = hintKey;
            Glyph = Geometry.Parse(glyph);
            Glyph.Freeze();
        }

        public string Name => LocalizationService.Get(_nameKey);

        public string Hint => LocalizationService.Get(_hintKey);

        /// <summary>The same marks the two-way switch drew: lines across, or columns down.</summary>
        public Geometry Glyph { get; }
    }
}
