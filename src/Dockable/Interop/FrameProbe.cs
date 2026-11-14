using System.Diagnostics;
using System.IO;
using System.Text;

namespace Dockable.Interop;

/// <summary>
/// Opt-in frame / read-back profiler. Inert when off — the hot paths only read one static bool.
///
/// Enabled either by the environment variable <c>DOCKABLE_FRAME_LOG=1</c> or by dropping a file named
/// <c>frame-log.on</c> into %APPDATA%\Dockable (so it can be toggled without touching the environment).
/// Records one CSV line per entry into %APPDATA%\Dockable\frame.log:
///
/// <code>
/// F,&lt;cost-µs&gt;,   one processed render-loop frame: time spent inside the loop body
/// R,&lt;cost-µs&gt;,   one screen read-back (a BitBlt of a maximised 4K window costs tens of ms)
/// I,&lt;text&gt;,       a one-off note (startup geometry etc.)
/// </code>
///
/// Writing is buffered and flushed in batches, so a frame never pays for disk I/O; the buffer is
/// flushed on every batch boundary and when the process exits.
/// </summary>
public static class FrameProbe
{
    /// <summary>True when profiling was requested for this run.</summary>
    public static readonly bool Enabled = Detect();

    // Small batch: ~2 s of frames at 30 fps, so nothing is lost if the process is killed without a
    // graceful shutdown, and one ~1.5 KB append per batch is far too cheap to show up in a frame.
    private const int FlushEvery = 64;

    private static readonly object Gate = new();
    private static readonly StringBuilder Pending = new();
    private static readonly double TicksToUs = 1_000_000.0 / Stopwatch.Frequency;

    private static StreamWriter? _writer;
    private static int _pendingLines;
    private static bool _openFailed;

    private static string DataDir
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dockable");

    private static string LogPath => Path.Combine(DataDir, "frame.log");

    private static bool Detect()
    {
        try
        {
            string? env = Environment.GetEnvironmentVariable("DOCKABLE_FRAME_LOG");
            if (env is not null)
            {
                string v = env.Trim().ToLowerInvariant();
                if (v is "1" or "true" or "on" or "yes")
                    return true;
            }
            return File.Exists(Path.Combine(DataDir, "frame-log.on"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Times one processed render-loop frame.</summary>
    public static void RecordFrame(long startTimestamp) => Record('F', startTimestamp);

    /// <summary>Times one screen read-back (the BitBlt in <c>WindowCapture</c>).</summary>
    public static void RecordReadback(long startTimestamp) => Record('R', startTimestamp);

    /// <summary>One-off note, e.g. the window geometry at startup.</summary>
    public static void Note(string text)
    {
        if (!Enabled)
            return;
        Append($"I,{Sanitize(text)}");
    }

    private static void Record(char kind, long startTimestamp)
    {
        if (!Enabled)
            return;
        long us = (long)((Stopwatch.GetTimestamp() - startTimestamp) * TicksToUs);
        Append($"{kind},{us}");
    }

    private static void Append(string line)
    {
        lock (Gate)
        {
            Pending.Append(line).Append('\n');
            if (++_pendingLines >= FlushEvery)
                FlushLocked();
        }
    }

    private static void FlushLocked()
    {
        if (_pendingLines == 0)
            return;

        string text = Pending.ToString();
        Pending.Clear();
        _pendingLines = 0;

        try
        {
            if (_writer is null)
            {
                if (_openFailed)
                    return;
                Directory.CreateDirectory(DataDir);
                _writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite), new UTF8Encoding(false));
            }
            _writer.Write(text);
            _writer.Flush();
        }
        catch
        {
            // Profiling must never take the dock down; a failed log leaves the app running normally.
            _openFailed = true;
            _writer = null;
        }
    }

    /// <summary>Flushes whatever is buffered (called on shutdown).</summary>
    public static void Close()
    {
        if (!Enabled)
            return;
        lock (Gate)
            FlushLocked();
    }

    private static string Sanitize(string text)
        => text.Replace('\n', ' ').Replace('\r', ' ');
}
