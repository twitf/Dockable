using System.Diagnostics;
using System.Windows.Threading;
using Dockable.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Dockable.Genie;

/// <summary>
/// Keeps a recent full-window capture of each app window while it is visible, so that when
/// a window minimizes we already have its image and bounds — capturing at minimize time is
/// unreliable because the window is usually already gone by the time the event arrives.
///
/// Captures happen on foreground changes and on a light refresh timer (only the current
/// foreground window), keeping cost low while staying reasonably fresh.
/// </summary>
public sealed class WindowThumbnailCache : IDisposable
{
    private const int MaxEntries = 12;
    // 1200 -> 2500. Each refresh is a screen-DC read-back of the whole foreground window, and the
    // cache only feeds the minimize warp / a minimized tile, both of which tolerate a 2.5 s stale
    // image. Since 08673b6 the blit runs on its own below-normal thread and the pixels are scaled down
    // inside the blit (WindowCapture.MaxCaptureDim), so one refresh costs a few ms rather than the
    // 40-112 ms it used to - but the DWM composition it forces still lands on the dock's next present,
    // which is why it stays this infrequent rather than going per-second.
    private const int RefreshMs = 2500;
    // Wait for a newly-focused window to actually be raised on top and repainted before
    // capturing it; capturing immediately on the focus event can grab the windows that
    // were still covering it.
    private const int FocusSettleMs = 180;

    private readonly Dictionary<IntPtr, WindowCapture.Result> _cache = new();
    private readonly LinkedList<IntPtr> _order = new(); // LRU: front = oldest
    private readonly WinEventHook _hook;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _settleTimer;
    private readonly uint _ownProcessId;
    private readonly Dispatcher _ui;
    private readonly object _gate = new();
    private bool _capturing;
    private bool _disposed;

    public WindowThumbnailCache()
    {
        _hook = new WinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND,
            OnForeground, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        _ownProcessId = (uint)Environment.ProcessId;
        _ui = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(RefreshMs) };
        _timer.Tick += (_, _) => CaptureForeground();
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FocusSettleMs) };
        _settleTimer.Tick += (_, _) => { _settleTimer.Stop(); CaptureForeground(); };
    }

    public void Start()
    {
        if (_hook.IsActive)
            return;
        _hook.Start();
        _timer.Start();
        CaptureForeground();
    }

    /// <summary>When set and returning true, the proactive refresh skips capturing — set by the dock while
    /// a minimize/restore warp is in flight, so a full-window BitBlt (tens of ms for a 4K window, on the UI
    /// thread) never lands mid-animation and stutters it. The warp already has the capture it needs.</summary>
    public Func<bool>? ShouldSuspend { get; set; }

    /// <summary>The most recent capture taken while <paramref name="hwnd"/> was visible, if any.</summary>
    public WindowCapture.Result? TryGet(IntPtr hwnd)
        => _cache.TryGetValue(hwnd, out var result) ? result : null;

    public void Remove(IntPtr hwnd)
    {
        if (_cache.Remove(hwnd))
            _order.Remove(hwnd);
    }

    private void OnForeground(HWND hwnd, uint @event)
    {
        // Capture is deferred (debounced) so the window has time to settle on top first. (OS
        // transitions are NOT touched here any more: the dock suppresses them just-in-time around its
        // own minimizes, so the focused window keeps the native maximize/snap animations.)
        _settleTimer.Stop();
        _settleTimer.Start();
    }

    private void CaptureForeground()
    {
        if (ShouldSuspend?.Invoke() == true)
        {
            // Not now: re-arm the settle timer so the capture lands just after the animation ends,
            // instead of being dropped for a whole refresh interval (the tile would keep a missing or
            // stale preview until the next tick). Re-arming is free when nothing is animating.
            _settleTimer.Stop();
            _settleTimer.Start();
            return;
        }
        BeginCapture(PInvoke.GetForegroundWindow());
    }

    /// <summary>
    /// Runs the screen read-back on a worker thread. Measured on a 4K @150% desktop before the
    /// down-scaled blit landed, one capture of a maximised window cost <b>40-112 ms</b> (the probe
    /// still records it as an <c>R</c> line) — as a DispatcherTimer handler that was a dead freeze of
    /// the UI thread, which is exactly the stutter this cache injected into the dock's own animation.
    /// The result is built and frozen on the worker, then handed back to the dock's thread to store.
    /// </summary>
    private void BeginCapture(HWND hwnd)
    {
        if (!WindowFilter.IsEligibleAppWindow(hwnd, _ownProcessId))
            return;

        lock (_gate)
        {
            if (_capturing || _disposed)
                return; // one read-back in flight; a skipped refresh is just a slightly older image
            _capturing = true;
        }

        // Below normal priority, like BackdropCapturer's read-back thread: one capture of a maximised
        // 4K window is 100-400 ms of CPU plus a forced DWM sync, and the moment it fires — a foreground
        // change — is exactly when a just-launched game is saturating the machine. At the pool's default
        // priority this capture competes with it for cores; below normal the scheduler defers it and the
        // preview simply lands a beat later.
        System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            System.Threading.Thread.CurrentThread.Priority = System.Threading.ThreadPriority.BelowNormal;
            long started = FrameProbe.Enabled ? Stopwatch.GetTimestamp() : 0;
            WindowCapture.Result? captured = null;
            try
            {
                captured = WindowCapture.Capture((IntPtr)hwnd);
            }
            catch
            {
                // A failed capture keeps the previous entry; it must never take the dock down.
            }
            finally
            {
                if (FrameProbe.Enabled)
                    FrameProbe.RecordReadback(started);
            }

            var result = captured;
            try
            {
                _ui.InvokeAsync(() =>
                {
                    lock (_gate)
                    {
                        _capturing = false;
                        if (_disposed)
                            return;
                        if (result.HasValue)
                            Store((IntPtr)hwnd, result.Value);
                    }
                });
            }
            catch
            {
                // The dispatcher is already shutting down: nothing left to store on, and this worker must
                // not surface an exception of its own during teardown.
                lock (_gate)
                    _capturing = false;
            }
        }, System.Threading.Tasks.TaskCreationOptions.LongRunning);
    }


    private void Store(IntPtr hwnd, WindowCapture.Result result)
    {
        if (_cache.ContainsKey(hwnd))
            _order.Remove(hwnd);
        _cache[hwnd] = result;
        _order.AddLast(hwnd);

        while (_order.Count > MaxEntries)
        {
            IntPtr oldest = _order.First!.Value;
            _order.RemoveFirst();
            _cache.Remove(oldest);
        }
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true; // a capture still in flight drops its result instead of storing it

        _timer.Stop();
        _settleTimer.Stop();

        _hook.Dispose();
    }
}
