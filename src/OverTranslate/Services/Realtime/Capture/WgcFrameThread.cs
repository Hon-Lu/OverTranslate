using System.Diagnostics;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.System;
using NLog;

namespace OverTranslate.Services.Realtime.Capture;

/// <summary>
/// The one thread a capture backend's frames, frame pool, capture session and Direct3D readback live
/// on, and the frame kept there for a timer when it arrived too soon to be read.
/// </summary>
/// <remarks>
/// <b>Only this thread touches the capture objects and the device.</b> Two reasons, both learned the
/// hard way:
///
/// On Windows 10 a free-threaded pool raises <c>FrameArrived</c> while it holds the Direct3D device's
/// own lock. 2.7.0 read a kept frame back on the polling thread, which then waited for that lock
/// inside <c>Unmap</c> while the handler waited for the poll — and realtime translation froze about a
/// second into almost every session. And on Windows 10 capture objects belong to the apartment that
/// created them: closing a session from any other thread fails with <c>RPC_E_WRONG_THREAD</c> and
/// leaves it running.
///
/// The fix for the first, in 2.7.0 and 2.7.1, still read back on the polling thread, just never
/// while the handler did. Those are also the versions with reports of the whole machine locking up on
/// AMD graphics — nothing ties the two together, but two threads taking turns on one device context
/// is the only graphics change those versions made, and a driver is where such a hang would live.
/// Dropping the second thread altogether was measured and lost the last frame of a burst (a page
/// scrolled just after something else repainted stayed wrong until the next repaint). So the frame
/// that comes too soon is kept here, on the same thread, and a timer on the same thread reads it.
///
/// The pool is created with <c>Direct3D11CaptureFramePool.Create</c> on this thread, so its frames
/// are delivered through this thread's queue rather than from inside the capture stack. Polls only
/// ever see bitmaps already in memory.
/// </remarks>
[SupportedOSPlatform("windows10.0.18362.0")]
internal sealed class WgcFrameThread
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    private readonly DispatcherQueueController _controller;
    private readonly DispatcherQueue _queue;
    private readonly Action<Direct3D11CaptureFrame> _readHeld;

    // Both touched only on this thread.
    private DispatcherQueueTimer? _timer;
    private Direct3D11CaptureFrame? _held;

    /// <param name="readHeld">
    /// Reads back a kept frame once its time has come, on this thread. Owns the frame: must dispose it.
    /// </param>
    public WgcFrameThread(Action<Direct3D11CaptureFrame> readHeld)
    {
        _readHeld = readHeld;
        _controller = DispatcherQueueController.CreateOnDedicatedThread();
        _queue = _controller.DispatcherQueue;
    }

    /// <summary>Whether the caller is on this thread.</summary>
    public bool IsCurrent => _queue.HasThreadAccess;

    /// <summary>
    /// Runs <paramref name="work"/> on this thread and waits for it. Throws <see cref="TimeoutException"/>
    /// when it has not finished in time — the thread is stuck in a call that is not coming back, and
    /// the caller must not be stuck with it.
    /// </summary>
    public T Invoke<T>(Func<T> work, TimeSpan timeout)
    {
        if (IsCurrent) return work();

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.TryEnqueue(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            }))
            throw new ObjectDisposedException(nameof(WgcFrameThread));

        if (!done.Task.Wait(timeout))
            throw new TimeoutException($"the capture thread did not finish within {timeout.TotalSeconds}s");
        return done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Queues <paramref name="work"/> on this thread. False once the thread is shutting down.</summary>
    public bool Post(Action work) => _queue.TryEnqueue(() => Guard(work, "queued work"));

    /// <summary>
    /// Keeps <paramref name="frame"/> and reads it back after <paramref name="due"/>, unless a newer
    /// frame or <see cref="ReleaseHeld"/> comes first. At most one frame is kept, so the pool always
    /// has a buffer left to compose the next one into. This thread only.
    /// </summary>
    public void Hold(Direct3D11CaptureFrame frame, TimeSpan due)
    {
        Debug.Assert(IsCurrent);
        ReleaseHeld();
        _held = frame;

        if (_timer is null)
        {
            _timer = _queue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += OnTimer;
        }

        // At least a millisecond: a zero interval is not a promise to run at once.
        _timer.Interval = due > TimeSpan.FromMilliseconds(1) ? due : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    /// <summary>Lets go of the kept frame, if any. This thread only.</summary>
    public void ReleaseHeld()
    {
        Debug.Assert(IsCurrent);
        _timer?.Stop();
        var held = _held;
        _held = null;
        held?.Dispose();
    }

    private void OnTimer(DispatcherQueueTimer sender, object args)
    {
        var held = _held;
        _held = null;
        if (held is not null) Guard(() => _readHeld(held), "the kept frame");
    }

    // An exception escaping a callback on this thread ends the process, and nothing that goes wrong
    // with one frame or one re-attach is worth that.
    private static void Guard(Action work, string what)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Realtime capture thread: {What} failed", what);
        }
    }

    /// <summary>
    /// Ends this thread: on it, stops the timer, lets go of the kept frame and runs
    /// <paramref name="teardown"/> — the pool and session are closed there, the only thread allowed
    /// to — and then shuts the queue down, which ends the thread. False if any of it did not finish
    /// within <paramref name="timeout"/>; the thread is then left to finish on its own.
    /// </summary>
    public bool Shutdown(Action teardown, TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();

        void Close()
        {
            ReleaseHeld();
            if (_timer is { } timer) timer.Tick -= OnTimer;
            _timer = null;
            teardown();
        }

        if (IsCurrent)
        {
            // Asked from one of this thread's own callbacks. Nothing can be waited for here, the
            // queue included: it ends once this callback returns.
            Guard(Close, "teardown");
            _ = _controller.ShutdownQueueAsync();
            return true;
        }

        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_queue.TryEnqueue(() =>
            {
                Guard(Close, "teardown");
                closed.SetResult();
            }))
        {
            if (!closed.Task.Wait(timeout)) return false;
        }

        var left = timeout - Stopwatch.GetElapsedTime(started);
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return _controller.ShutdownQueueAsync().AsTask().Wait(left);
    }
}
