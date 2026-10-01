using System.Text.RegularExpressions;

namespace OverTranslate.Translation.Google;

/// <summary>
/// Reads the key <see cref="GoogleChromeTranslator"/> needs out of Google's own translate element,
/// so that no copy of it is kept in this program.
/// </summary>
/// <remarks>
/// <para>Two requests, in the order a browser makes them: the loader at
/// <see cref="LoaderUrl"/> (about 3 KB), which names the element's main script, and that script
/// (about 300 KB), which calls <c>/v1/translateHtml</c> with the key in an
/// <c>X-goog-api-key</c> header. The loader carries no key itself.</para>
///
/// <para>The main script holds more than one key — another one asks for the list of supported
/// languages, and is refused by translateHtml with a 403 — so the one written next to
/// <c>translateHtml</c> comes first and the rest follow, for when Google moves things around and
/// that one can no longer be told apart.</para>
///
/// <para>Both scripts are minified and undocumented, which is why this is only asked when there is
/// no key or the one there was is refused, and why a failure here is an ordinary engine failure
/// that the option's backups answer for.</para>
/// </remarks>
internal static partial class GoogleChromeKeySource
{
    internal const string LoaderUrl = "https://translate.googleapis.com/translate_a/element.js?cb=_";

    /// <summary>
    /// The keys found, the likeliest first. Throws when either script cannot be had or read.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FetchAsync(HttpClient http, CancellationToken cancellationToken)
    {
        var loader = await http.GetStringAsync(LoaderUrl, cancellationToken);
        var main = FindMainScript(loader) ?? throw new FormatException("no main script in the loader");

        var keys = FindKeys(await http.GetStringAsync(main, cancellationToken));
        return keys.Count > 0 ? keys : throw new FormatException("no key in the main script");
    }

    /// <summary>The main script's address, from the loader's JavaScript-escaped settings.</summary>
    internal static string? FindMainScript(string loader)
    {
        var unescaped = loader
            .Replace(@"\/", "/")
            .Replace(@"\\u003d", "=")
            .Replace(@"=", "=");

        var match = MainScriptPattern().Match(unescaped);
        return match.Success ? match.Value : null;
    }

    /// <summary>Every key in the main script, the one sent to translateHtml first.</summary>
    internal static IReadOnlyList<string> FindKeys(string main)
    {
        var keys = KeyPattern().Matches(main).Select(m => (m.Value, m.Index)).ToList();

        // How close "translateHtml" is written before each key: the request that calls it lists
        // its path, then its headers.
        var path = main.IndexOf("translateHtml", StringComparison.Ordinal);
        return keys
            .OrderBy(k => path >= 0 && k.Index > path ? k.Index - path : int.MaxValue)
            .Select(k => k.Value)
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"https://translate\.googleapis\.com/_/translate_http/_/js/[^""'\\\s]+")]
    private static partial Regex MainScriptPattern();

    [GeneratedRegex(@"AIza[0-9A-Za-z_\-]{35}")]
    private static partial Regex KeyPattern();
}
