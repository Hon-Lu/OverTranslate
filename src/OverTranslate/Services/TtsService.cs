using System.IO;
using System.Net.Http;
using System.Windows.Media;
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
    // translation's (TranslationTiming): one slow engine hands over to the next after ten seconds,
    // where GTranslate's own clients waited a hundred.
    private static readonly HttpClient Http = EngineHttp.CreateClient(TranslationTiming.Request);

    // Google first, as before; RPC ahead of the old translate_tts address, which is the order the old
    // service used. Microsoft and Bing are the same Azure voices by two routes, and between them
    // cover the one language the application offers that Google has no voice for (Slovenian).
    // Yandex, the old fifth, was dropped: nothing reached it that the four above could not read.
    private static readonly SpeechSynthesizer Synthesizer = new(
    [
        new GoogleRpcSpeech(Http),
        new GoogleWebSpeech(Http),
        new MicrosoftSpeech(Http),
        new BingSpeech(Http),
    ]);

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

        try
        {
            // The token reaches the request itself, so 停止 aborts the download rather than only
            // discarding it when it arrives.
            var (audio, _) = await Synthesizer.SynthesizeAsync(text, language, token);

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
        catch
        {
            // Every engine failed — clear state so the button resets, and let the caller say so.
            SetActive(false);
            throw;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        System.Windows.Application.Current.Dispatcher.Invoke(ClosePlayer);
    }
}
