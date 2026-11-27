using System.IO;
using System.Net.Http;
using System.Text;
using OverTranslate.Models;
using OverTranslate.Services;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Bing;
using OverTranslate.Translation.Google;
using OverTranslate.Translation.Microsoft;

// 雙語互譯 on a pair written in one script (英文⇄法文 and the like): what the characters say, what the
// engine says, and where the application's own chain ends up, per text.
//
//   --bilingual <first> <second> <list.tsv> <out.tsv> [providers]
//
// list: <expected source code><TAB><text>, "*" for a text that is honestly either. providers is a
// comma list of microsoft, google, bing (default all three). Every request is counted, since what a
// wrong first guess costs is one more of them.
internal static class BilingualProbe
{
    /// <summary>
    /// --xlate-source &lt;source&gt; &lt;target&gt; &lt;text&gt;: one text through every free engine from a named
    /// source, since --xlate-line is fixed to English.
    /// </summary>
    internal static async Task<int> TranslateFrom(string[] args, HttpClient http)
    {
        var block = new List<OcrTextBlock> { new(string.Join(' ', args.Skip(2)), new System.Windows.Rect(0, 0, 100, 20)) };
        foreach (var (name, engine) in new (string, ITextTranslator)[]
                 {
                     ("Microsoft", new MicrosoftTranslator(http)),
                     ("Google Web", new GoogleWebTranslator(http)),
                     ("Google RPC", new GoogleRpcTranslator(http)),
                     ("Google Chrome", new GoogleChromeTranslator(http)),
                     ("Bing", new BingTranslator(http)),
                     ("Youdao", new OverTranslate.Translation.Youdao.YoudaoTranslator(http)),
                     ("TranSmart", new OverTranslate.Translation.Tencent.TranSmartTranslator(http)),
                     ("Yandex", new OverTranslate.Translation.Yandex.YandexTranslator(http)),
                 })
        {
            try
            {
                var (result, detected) = await new EngineProvider(engine).TranslateAsync(block, args[0], args[1], "");
                Console.WriteLine($"  [{name}] ({detected}) {result[0].TranslatedText}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{name}] FAILED: {ex.Message}");
            }
        }
        return 0;
    }

    internal static async Task<int> Run(string[] args, HttpClient http)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: --bilingual <first> <second> <list.tsv> <out.tsv> [microsoft,google,bing]");
            return 1;
        }

        var pair = new BilingualPair(args[0], args[1]);
        var rows = File.ReadAllLines(args[2], Encoding.UTF8)
            .Where(l => l.Trim().Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t', 2))
            .ToList();
        var names = (args.Length > 4 ? args[4] : "microsoft,google,bing").Split(',');

        using var output = new StreamWriter(args[3], false, new UTF8Encoding(false));
        output.WriteLine("provider\texpected\ttext\tguessed\tdetected_first\tfinal_target\tfinal_source\trequests\ttranslation");
        foreach (var name in names)
        {
            var provider = new EngineProvider(name switch
            {
                "microsoft" => new MicrosoftTranslator(http),
                "google" => (ITextTranslator)new GoogleWebTranslator(http),
                "bing" => new BingTranslator(http),
                _ => throw new ArgumentException(name),
            });

            foreach (var row in rows)
            {
                var text = row[1];
                var requests = 0;
                string? firstDetected = null;
                BilingualAnswer? answer = null;
                try
                {
                    answer = await BilingualTranslation.TranslateAsync(text, pair, async (source, target) =>
                    {
                        requests++;
                        var result = await provider.TranslateAsync(
                            [new OcrTextBlock(text, new System.Windows.Rect())],
                            BilingualTranslation.SourceCode(source), target, "");
                        firstDetected ??= result.DetectedLang;
                        return result;
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[{name}] {text}: {ex.Message}");
                }

                var guessed = BilingualDirection.ResolveSource(text, pair.First, pair.Second);
                output.WriteLine(string.Join('\t', name, row[0], text, guessed ?? "-", firstDetected ?? "-",
                    answer?.Target ?? "ERR", answer?.Source ?? "-", requests,
                    answer?.Blocks.FirstOrDefault()?.TranslatedText?.Replace('\t', ' ').Replace('\n', ' ') ?? ""));
                output.Flush();
                await Task.Delay(150);
            }
            Console.WriteLine($"{name} done");
        }
        return 0;
    }
}
