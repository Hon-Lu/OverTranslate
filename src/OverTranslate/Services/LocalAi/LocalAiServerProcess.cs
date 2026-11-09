using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OverTranslate.Services.LocalAi;

/// <summary>Starts <c>llama-server</c>, on a dedicated card and nowhere else.</summary>
/// <remarks>
/// <para>The device is named on the command line, having first been found among the dedicated cards:
/// left to choose, llama.cpp settles for whatever it finds, the CPU included, and reports no error
/// (<see cref="LocalAiGpu.ChooseDevice"/>). Named, a device it cannot use stops it from starting.</para>
///
/// <para>Listening on 127.0.0.1 alone, on a free port, and with a key made up for each start: without
/// one, any web page open in a browser could use the model, and with one everything but
/// <c>/health</c> answers 401. The key is passed in the environment, where other processes do not
/// see it, rather than on the command line, where they do.</para>
/// </remarks>
internal static class LocalAiServerProcess
{
    private const int HealthPollMilliseconds = 200;

    /// <summary>How long a model may take to load; a large one from a slow disk takes minutes.</summary>
    internal static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(5);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    internal static async Task<ILocalAiServerProcess> StartAsync(string serverPath, LocalAiLaunch launch, CancellationToken token)
    {
        var cards = await LocalAiGpu.DetectAsync().WaitAsync(token).ConfigureAwait(false);
        if (cards.Count == 0) throw new LocalAiNoGpuException();

        var device = LocalAiGpu.ChooseDevice(await ListDevicesAsync(serverPath, token).ConfigureAwait(false), cards)
                     ?? throw new LocalAiNoGpuException("llama-server lists none of the dedicated GPUs");

        var server = Running.Start(serverPath, launch.ModelPath, device);
        try
        {
            await WaitHealthyAsync(server, token).ConfigureAwait(false);
            return server;
        }
        catch
        {
            server.Kill();
            server.Dispose();
            throw;
        }
    }

    private static async Task<string> ListDevicesAsync(string serverPath, CancellationToken token)
    {
        using var process = new Process { StartInfo = Hidden(serverPath, "--list-devices") };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new LocalAiStartFailedException($"llama-server could not be run: {ex.Message}", ex);
        }

        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return await output.ConfigureAwait(false) + "\n" + await error.ConfigureAwait(false);
    }

    private static ProcessStartInfo Hidden(string serverPath, string arguments) => new(serverPath, arguments)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(serverPath)!,
    };

    private static async Task WaitHealthyAsync(Running server, CancellationToken token)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < StartLimit)
        {
            token.ThrowIfCancellationRequested();
            if (server.Exited.IsCompleted)
                throw new LocalAiStartFailedException($"llama-server exited while loading:\n{server.RecentLog}");

            try
            {
                using var answer = await Http.GetAsync(new Uri(server.BaseAddress, "health"), token).ConfigureAwait(false);
                if (answer.StatusCode == HttpStatusCode.OK) return;
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !token.IsCancellationRequested))
            {
                // Not listening yet, or too busy loading to answer.
            }

            await Task.Delay(HealthPollMilliseconds, token).ConfigureAwait(false);
        }

        throw new LocalAiStartFailedException($"llama-server was not ready in {StartLimit.TotalMinutes:F0} minutes:\n{server.RecentLog}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    private sealed class Running : ILocalAiServerProcess
    {
        private const int KeptLines = 60;

        private readonly Process _process;
        private readonly Queue<string> _log = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private Running(Process process, int port, string apiKey)
        {
            _process = process;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
            ApiKey = apiKey;
        }

        public Uri BaseAddress { get; }

        public string ApiKey { get; }

        public Task Exited => _exited.Task;

        public string RecentLog
        {
            get
            {
                lock (_log) return string.Join("\n", _log);
            }
        }

        internal static Running Start(string serverPath, string modelPath, string device)
        {
            var port = FreePort();
            var apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            var arguments = new StringBuilder()
                .Append("-m \"").Append(modelPath).Append('"')
                .Append(" --device ").Append(device)
                .Append(" --host 127.0.0.1 --port ").Append(port)
                .Append(" -c 4096 -ngl 99 -np 2 -kvu --no-webui")
                .ToString();

            var info = Hidden(serverPath, arguments);
            info.Environment["LLAMA_API_KEY"] = apiKey;

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var running = new Running(process, port, apiKey);
            process.OutputDataReceived += (_, e) => running.Remember(e.Data);
            process.ErrorDataReceived += (_, e) => running.Remember(e.Data);
            process.Exited += (_, _) => running._exited.TrySetResult();

            try
            {
                process.Start();
            }
            catch (Win32Exception ex)
            {
                process.Dispose();
                throw new LocalAiStartFailedException($"llama-server could not be run: {ex.Message}", ex);
            }

            ChildProcessJob.Attach(process);
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return running;
        }

        private void Remember(string? line)
        {
            if (line is null) return;
            lock (_log)
            {
                _log.Enqueue(line);
                while (_log.Count > KeptLines) _log.Dequeue();
            }
        }

        public void Kill() => TryKill(_process);

        public void Dispose() => _process.Dispose();
    }

    /// <summary>Ties a child's life to this process: when this one goes, however it goes, so does it.</summary>
    private static class ChildProcessJob
    {
        private const uint KillOnJobClose = 0x2000;
        private const int ExtendedLimitInformation = 9;

        private static readonly object Sync = new();
        // Held for the life of the process: Windows closes it when the process ends, and that kills the job.
        private static IntPtr _job;

        internal static void Attach(Process process)
        {
            lock (Sync)
            {
                if (_job == IntPtr.Zero)
                {
                    var job = CreateJobObjectW(IntPtr.Zero, null);
                    if (job == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = KillOnJobClose } };
                    if (!SetInformationJobObject(job, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    _job = job;
                }

                if (!AssignProcessToJobObject(_job, process.Handle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimits info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}
