using System.IO;
using System.IO.Compression;
using System.Text.Json;
using NLog;

namespace OverTranslate.Services.LocalAi;

/// <summary>The llama.cpp build the models run on: whether it is here, and getting it.</summary>
/// <remarks>
/// <para>Kept under <c>runtimes\llama.cpp\{version}</c> (<see cref="AppDataPaths.Runtimes"/>). One
/// zip, hashed before anything in it is unpacked; the folder only counts once a marker naming the
/// version has been written, so a download or an unpack cut off anywhere leaves "not here".</para>
///
/// <para>The Vulkan build is enough on every machine this feature runs on, and only what
/// <c>llama-server</c> loads is unpacked: the release's other tools are a third of its size.</para>
/// </remarks>
internal sealed class LocalAiRuntimeStore(LocalAiRuntime runtime, string root, LocalAiDownloader downloader)
{
    private string _root = root;

    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private const string ReadyMarker = "verified.json";
    private const string ServerExe = "llama-server.exe";

    internal static string DefaultRoot => Path.Combine(AppDataPaths.Runtimes, "llama.cpp");

    internal LocalAiRuntime Runtime { get; } = runtime;

    internal string Folder => Path.Combine(_root, Runtime.Version);

    /// <summary>See <see cref="LocalAiModelStore.Relocate"/>.</summary>
    internal void Relocate(string root) => _root = root;

    internal string ServerPath => Path.Combine(Folder, ServerExe);

    private string ZipPath => Path.Combine(Folder, "llama.zip");

    internal bool IsReady
    {
        get
        {
            try
            {
                var marker = Path.Combine(Folder, ReadyMarker);
                if (!File.Exists(marker) || !File.Exists(ServerPath)) return false;
                using var json = JsonDocument.Parse(File.ReadAllText(marker));
                return json.RootElement.GetProperty("version").GetString() == Runtime.Version;
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    /// <summary>Fetches and unpacks the build, unless it is already here.</summary>
    internal async Task EnsureAsync(Action<long>? progress = null, CancellationToken token = default)
    {
        if (IsReady) return;

        await downloader.FetchAsync(
            Runtime.Urls.Select(url => new Uri(url)).ToList(), ZipPath, Runtime.Size, Runtime.Sha256,
            progress, token: token).ConfigureAwait(false);

        await Task.Run(() => Unpack(token), token).ConfigureAwait(false);
        Log.Info("llama.cpp {Version} installed in {Folder}", Runtime.Version, Folder);
        RemoveOtherVersions();
    }

    /// <summary>Whether a release's file is one <c>llama-server</c> needs.</summary>
    /// <remarks>
    /// A name in a subfolder is refused along with the rest: the release is flat, and nothing is
    /// ever written outside the version's folder.
    /// </remarks>
    internal static bool IsNeeded(string entry)
    {
        if (entry.Contains('/') || entry.Contains('\\')) return false;
        if (entry.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)) return true;
        if (entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return entry.Equals(ServerExe, StringComparison.OrdinalIgnoreCase);
        if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        return !entry.StartsWith("llama-", StringComparison.OrdinalIgnoreCase)
               || !entry.EndsWith("-impl.dll", StringComparison.OrdinalIgnoreCase)
               || entry.Equals("llama-server-impl.dll", StringComparison.OrdinalIgnoreCase);
    }

    private void Unpack(CancellationToken token)
    {
        using (var zip = ZipFile.OpenRead(ZipPath))
        {
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                if (IsNeeded(entry.FullName))
                    entry.ExtractToFile(Path.Combine(Folder, entry.FullName), overwrite: true);
            }
        }

        if (!File.Exists(ServerPath))
            throw new InvalidDataException($"{ServerExe} is not in the llama.cpp release");

        File.WriteAllText(Path.Combine(Folder, ReadyMarker), JsonSerializer.Serialize(new { version = Runtime.Version }));
        TryDeleteFile(ZipPath);
    }

    /// <summary>Removes what an unfinished download left; what is ready is kept.</summary>
    internal void DiscardIncomplete()
    {
        if (IsReady) return;
        try
        {
            if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(ex, "The unfinished llama.cpp folder {Folder} could not be removed", Folder);
        }
    }

    // A new app version brings a new build; the old one is of no use to anything.
    private void RemoveOtherVersions()
    {
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(_root))
            {
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(Folder), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Info("An older llama.cpp could not be removed yet: {Message}", ex.Message);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
