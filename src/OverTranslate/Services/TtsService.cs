using System.IO;
using System.Net.Http;
using System.Windows.Media;
using OverTranslate.Models;
using OverTranslate.Services.Providers;
using OverTranslate.Translation;
using OverTranslate.Translation.Speech;
using NLog;

namespace OverTranslate.Services;

public class TtsService : IDisposable
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    // Every speaker in the application reads through one set of engines, so the Bing credentials
    // and the Microsoft token are fetched once rather than once per window. The request timeout is
    // translation's (TranslationTiming), where GTranslate's own clients waited a hundred seconds.
    //
    // Reaching a host gets three seconds of that (#436): where Google's packets are dropped —
    // mainland China, some office networks — a press otherwise waited the whole ten to be told so.
    // Ordinary connections from Taiwan, handshake included, took 65–300 ms. A connection that is
    // made and then answers slowly still has the ten. (Declared first: static fields are set in
    // the order they are written.)
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private static readonly HttpClient Http = EngineHttp.CreateClient(TranslationTiming.Request, ConnectTimeout);

    // Only the service the settings name reads (SpeechProviders.Resolve); a failure is shown as that
    // service's rather than read in another voice. Google's RPC address ahead of the old
    // translate_tts one, which is the order the old service used; both are the same voices.
    // Microsoft and Bing are the same Azure voices by two routes, and read every language the
    // application offers; Google has none for Slovenian or Persian.
    private static readonly Dictionary<SpeechProvider, SpeechSynthesizer> Synthesizers = new()
    {
        [SpeechProvider.Google] = new(SpeechProviders.Display(SpeechProvider.Google),
            [new GoogleRpcSpeech(Http), new GoogleWebSpeech(Http)]),
        [SpeechProvider.Microsoft] = new(SpeechProviders.Display(SpeechProvider.Microsoft), [new MicrosoftSpeech(Http)]),
        [SpeechProvider.Bing] = new(SpeechProviders.Display(SpeechProvider.Bing), [new BingSpeech(Http)]),
    };

    private MediaPlayer? _player;
    private string? _currentFile;
    private CancellationTokenSource? _cts;
    private bool _active;

    /// <summary>True while fetching audio or playing. Lets the UI toggle a play/stop button.</summary>
    public bool IsActive => _active;

    /// <summary>Raised (on the UI thread) whenever playback starts, ends, fails, or is stopped.</summary>
    public event EventHandler? StateChanged;

    /// <summary>
    /// Creates the player on demand. A MediaPlayer that has raised MediaFailed cannot be trusted to
    /// play again — depending on the error it can go silently dead, and then every later Open/Play on
    /// that instance does nothing at all, which is exactly the "no sound until I reopen 取詞翻譯" the
    /// user is left with. So a failed player is thrown away and the next request gets a fresh one.
    /// UI thread only.
    /// </summary>
    private MediaPlayer EnsurePlayer()
    {
        if (_player != null) return _player;

        var player = new MediaPlayer();
        // Natural end / playback error must flip the button back to "play".
        player.MediaEnded += (_, _) =>
        {
            if (!ReferenceEquals(_player, player)) return;
            // Close() releases the temp file, which the player holds open until its next Open().
            player.Close();
            DeleteCurrentFile();
            SetActive(false);
        };
        player.MediaFailed += (_, e) =>
        {
            Log.Warn(e.ErrorException, "TTS playback failed, discarding player");
            player.Close();
            if (ReferenceEquals(_player, player))
            {
                _player = null;
                DeleteCurrentFile();
            }
            SetActive(false);
        };

        _player = player;
        return player;
    }

    /// <summary>Stops playback and releases the file the player was holding. UI thread only.</summary>
    private void ClosePlayer()
    {
        if (_player != null)
        {
            _player.Stop();
            _player.Close();
        }
        DeleteCurrentFile();
    }

    private void SetActive(bool value)
    {
        if (_active == value) return;
        _active = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private const string TempPrefix = "overtranslate_tts_";
    private static int _staleFilesSwept;

    /// <summary>
    /// Every playback gets its own file: the player keeps the previous one open, and re-opening the
    /// same path also runs into the media stack caching that URI.
    /// </summary>
    private static string NewTempFile() =>
        Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.mp3");

    private void DeleteCurrentFile()
    {
        var file = Interlocked.Exchange(ref _currentFile, null);
        if (file == null) return;
        try { File.Delete(file); }
        catch (Exception ex) { Log.Debug(ex, "Could not delete TTS temp file {File}", file); }
    }

    /// <summary>Removes files an earlier crash left behind. Old enough that no live player holds them.</summary>
    private static void SweepStaleFilesOnce()
    {
        if (Interlocked.Exchange(ref _staleFilesSwept, 1) != 0) return;
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), TempPrefix + "*.mp3"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
                catch { /* still in use by another process, or already gone */ }
            }
        }
        catch (Exception ex) { Log.Debug(ex, "TTS temp sweep failed"); }
    }

    /// <summary>Stops any in-flight fetch and playback.</summary>
    public void Stop()
    {
        _cts?.Cancel();
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(false);
    }

    /// <exception cref="SpeechFailedException">
    /// The service could not read it; <see cref="FailureText"/> says why, for the user.
    /// </exception>
    public async Task SpeakAsync(string text, string langCode)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
        SetActive(true);

        SweepStaleFilesOnce();

        // The same codes translation speaks, so every language the pickers offer has its voice —
        // the old table knew fifteen and read the rest in English. 自動 is no language to read in;
        // the callers never offer it, and if one ever does, nothing is better than a wrong voice.
        var language = EngineLanguage.SourceToEngine(langCode);
        if (language is null)
        {
            Log.Debug("TTS skipped: no language to read {Lang} in", langCode);
            SetActive(false);
            return;
        }

        var provider = SpeechProviders.Resolve(SettingsService.Instance.Current);
        try
        {
            // The token reaches the request itself, so 停止 aborts the download rather than only
            // discarding it when it arrives.
            var (audio, _) = await Synthesizers[provider].SynthesizeAsync(text, language, token);

            var file = NewTempFile();
            await File.WriteAllBytesAsync(file, audio, token);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                DeleteCurrentFile();
                _currentFile = file;
                var player = EnsurePlayer();
                player.Open(new Uri(file));
                player.Play();
            });
        }
        catch (Exception) when (token.IsCancellationRequested)
        {
            // Stopped, or replaced by a newer press — which owns the button state now.
        }
        catch (SpeechFailedException)
        {
            // Clear state so the button resets, and let the caller say so.
            SetActive(false);
            throw;
        }
        catch (Exception ex)
        {
            // Writing the file, or the player: not the service's doing, but the same to the user.
            SetActive(false);
            throw new SpeechFailedException(SpeechProviders.Display(provider), SpeechFailure.Unavailable, ex.Message, ex);
        }
    }

    /// <summary>
    /// What to tell the user about a failure <see cref="SpeakAsync"/> threw, reading
    /// <paramref name="langCode"/>.
    /// </summary>
    public static string FailureText(Exception ex, string langCode)
    {
        var failure = ex as SpeechFailedException;
        var service = failure?.Service ?? SpeechProviders.Display(SpeechProviders.Resolve(SettingsService.Instance.Current));

        return failure?.Reason switch
        {
            SpeechFailure.Unreachable => LocalizationService.Format("S.Tts.Unreachable", service),
            SpeechFailure.Unsupported => LocalizationService.Format("S.Tts.Unsupported", service, LanguageName(langCode)),
            _ => LocalizationService.Format("S.Tts.Unavailable", service),
        };
    }

    // Source and target lists both, since either side's speaker reads: their codes differ (ZH beside
    // ZH-HANS, EN beside EN-US), their names do not.
    private static string LanguageName(string code) =>
        LanguageData.SourceLanguages.Concat(LanguageData.TargetLanguages)
            .FirstOrDefault(language => language.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.ShortName
        ?? code;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
    }
}
