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
    // 1200 -> 2500. Each refresh is a screen-DC BitBlt read-back of the whole foreground window
    // (tens of ms for a maximised 4K window, on the UI thread), and the cache only feeds the
    // minimize animation — which tolerates a 2.5 s stale image.
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
            return; // a warp is animating, or the dock is itself animating — don't read back now
        BeginCapture(PInvoke.GetForegroundWindow());
    }

    /// <summary>
    /// Runs the screen read-back on a worker thread. Measured on a 4K @150% desktop, one capture of a
    /// maximised window costs <b>100-400 ms</b> — as a DispatcherTimer handler that was a dead freeze of
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

        System.Threading.Tasks.Task.Run(() =>
        {
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
                        if (result is not null)
                            Store((IntPtr)hwnd, result);
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
        });
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
