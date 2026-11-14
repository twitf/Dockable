using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;

namespace Dockable.Genie;

/// <summary>
/// Captures a window's on-screen pixels by BitBlt-ing the composited screen region it
/// occupies. Unlike <c>PrintWindow</c> (which returns a black client area for many
/// hardware-accelerated apps, leaving only the caption frame), this grabs exactly what
/// the user sees — including GPU-rendered content — as long as the window is visible and
/// not occluded, which holds for a window the user is actively minimizing.
/// </summary>
public static class WindowCapture
{
    // CAPTUREBLT makes the blit include layered / composited windows.
    private const ROP_CODE CaptureRop = ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT;

    /// <summary>Longest edge, in physical pixels, a window capture is kept at. Nothing reads it at
    /// native resolution: a tile is ~80 DIP (120 px at 150%), a preview popup a few hundred, and the
    /// genie warp only stretches it while it shrinks into the tile. Capturing a maximised 4K window at
    /// full resolution wrote 33 MB three times over (the DIB, the alpha pass, then WritePixels) and
    /// measured 40-112 ms on this machine - the dock's single biggest recurring cost, once every
    /// 2.5 s. The down-scale happens inside the blit, so no extra pass is paid for it.</summary>
    private const int MaxCaptureDim = 768;

    /// <summary>HALFTONE: average the source pixels instead of dropping them. At a 5x down-scale
    /// (4K to 768) point sampling would leave visible noise that every later stage inherits.</summary>
    private const int HalftoneStretchMode = 4;

    public readonly record struct Result(BitmapSource Bitmap, Int32Rect ScreenRectPx);

    /// <summary>
    /// Captures an arbitrary screen rectangle (physical pixels) by BitBlt-ing the composited screen.
    /// With <c>WDA_EXCLUDEFROMCAPTURE</c> set on the dock, the dock is omitted from the grab, so this
    /// yields the true backdrop behind it (for the Liquid Glass refraction). Null on failure.
    /// </summary>
    public static BitmapSource? CaptureScreenRect(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return null;
        return BlitScreenRect(x, y, w, h, BuildBackdropBitmap);
    }

    /// <summary>Runs the shared screen-DC → memory-DC → top-down 32bpp DIB → BitBlt(CAPTUREBLT)
    /// dance for a screen rectangle, handing the raw BGRX bits to <paramref name="build"/> while the
    /// DIB is still selected/alive (it may mutate them in place). All GDI objects are released in
    /// the same finally order as before the extraction. Null when any GDI step or the blit fails.</summary>
    private static unsafe BitmapSource? BlitScreenRect(int x, int y, int w, int h,
        Func<IntPtr, int, int, BitmapSource?> build)
        => BlitScreenRect(x, y, w, h, w, h, build);

    /// <summary>As above, but into a <paramref name="dw"/>×<paramref name="dh"/> bitmap, letting GDI
    /// do the down-scale during the blit (StretchBlt) instead of copying the full-size pixels first.</summary>
    private static unsafe BitmapSource? BlitScreenRect(int x, int y, int w, int h, int dw, int dh,
        Func<IntPtr, int, int, BitmapSource?> build)
    {
        HDC screenDc = PInvoke.GetDC((HWND)IntPtr.Zero);
        HDC memDc = PInvoke.CreateCompatibleDC(screenDc);
        try
        {
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            bmi.bmiHeader.biWidth = dw;
            bmi.bmiHeader.biHeight = -dh; // negative => top-down rows
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;
            bmi.bmiHeader.biCompression = 0; // BI_RGB

            void* bits;
            HBITMAP dib = PInvoke.CreateDIBSection(memDc, &bmi, DIB_USAGE.DIB_RGB_COLORS, &bits, default, 0);
            if (dib.IsNull || bits is null)
                return null;

            HGDIOBJ previous = PInvoke.SelectObject(memDc, (HGDIOBJ)(void*)dib);
            try
            {
                bool blitted;
                if (dw == w && dh == h)
                {
                    blitted = PInvoke.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, CaptureRop);
                }
                else
                {
                    // Mode applies to the next StretchBlt; the brush origin is reset because HALFTONE
                    // otherwise aligns its dither pattern to whatever was set last, leaving streaks.
                    SetStretchBltMode(memDc, HalftoneStretchMode);
                    SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
                    blitted = StretchBlt(memDc, 0, 0, dw, dh, screenDc, x, y, w, h, (uint)CaptureRop);
                }

                if (!blitted)
                    return null;
                return build((IntPtr)bits, dw, dh);
            }
            finally
            {
                PInvoke.SelectObject(memDc, previous);
                PInvoke.DeleteObject((HGDIOBJ)(void*)dib);
            }
        }
        finally
        {
            PInvoke.DeleteDC(memDc);
            PInvoke.ReleaseDC((HWND)IntPtr.Zero, screenDc);
        }
    }

    // Hand-declared rather than added to the CsWin32 allow-list (NativeMethods.txt): two GDI entry
    // points, everything else in this file already goes through PInvoke.
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool StretchBlt(HDC hdcDest, int xDest, int yDest, int wDest, int hDest,
        HDC hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int SetStretchBltMode(HDC hdc, int mode);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetBrushOrgEx(HDC hdc, int x, int y, IntPtr point);

    /// <summary>The Liquid Glass backdrop bitmap: the raw BGRX bits as an opaque Bgr32 image.</summary>
    private static BitmapSource BuildBackdropBitmap(IntPtr bits, int w, int h)
    {
        int stride = w * 4;
        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), bits, stride * h, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static unsafe Result? Capture(IntPtr hwnd)
    {
        long startTs = MinimizeProfiler.Enabled ? Stopwatch.GetTimestamp() : 0;
        var window = (HWND)hwnd;

        // An iconic window keeps no composited surface: DWM answers with its icon-state placeholder
        // rect (off-screen, a ~5:1 sliver) and the BitBlt of that area is all zeros. That black frame
        // used to become a minimised tile's preview, which is the "black bar" that shows up in the
        // dock. Callers must already hold a capture taken while the window was visible — refuse
        // here, so no current OR future caller can reintroduce it.
        if (PInvoke.IsIconic(window))
            return null;

        // The DWM "extended frame bounds" exclude the drop shadow and the invisible resize border, so
        // we capture the window the user actually sees — not a margin of backdrop/shadow around it.
        // Fall back to GetWindowRect on older OSes / failure.
        RECT rect;
        var frame = default(RECT);
        var hr = PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
            &frame, (uint)sizeof(RECT));
        if (hr.Succeeded && frame.right > frame.left && frame.bottom > frame.top)
            rect = frame;
        else if (!PInvoke.GetWindowRect(window, &rect))
            return null;

        int w = rect.right - rect.left;
        int h = rect.bottom - rect.top;
        if (w <= 0 || h <= 0 || w > 20000 || h > 20000)
            return null;

        // A rect that cannot be a real window: too small to hold one, or a ratio no window has. This
        // catches the other placeholder shapes (hidden/destroying windows) before they cost a blit.
        if (w < 64 || h < 64 || w > 8 * h || h > 8 * w)
            return null;

        int radiusPx = CornerRadiusPx(window, w, h);

        // Blit at (at most) MaxCaptureDim on the long edge. The pixels are only ever shown inside a
        // tile or a popup, so full resolution bought nothing but a 33 MB round trip per capture.
        double shrink = Math.Min(1.0, MaxCaptureDim / (double)Math.Max(w, h));
        int cw = Math.Max(1, (int)Math.Round(w * shrink));
        int ch = Math.Max(1, (int)Math.Round(h * shrink));
        int carvePx = shrink < 1.0 ? Math.Max(1, (int)Math.Round(radiusPx * shrink)) : radiusPx;

        // Copy the composited screen pixels at the window's location, then carve the corners in place.
        var bitmap = BlitScreenRect(rect.left, rect.top, w, h, cw, ch, (bits, bw, bh) =>
        {
            // A frame that is blank everywhere means the blit landed on empty DWM surface (mid-
            // transition, hidden, or the placeholder shapes above). Storing it would overwrite a good
            // preview with a black one, so it is refused here instead.
            if (IsBlankFrame(bits, bw, bh))
                return null;
            var built = BuildWindowBitmap(bits, bw, bh, carvePx);
            if (MinimizeProfiler.Enabled)
                MinimizeProfiler.Mark("capture", Stopwatch.GetElapsedTime(startTs).TotalMilliseconds, bw, bh);
            return built;
        });
        return bitmap is null ? null : new Result(bitmap, new Int32Rect(rect.left, rect.top, w, h));
    }

    /// <summary>Reports the captured frame as blank when every pixel of a 12×12 sample grid is pure
    /// black. A real window always has some non-black pixel (its chrome at the very least), so a fully
    /// black grid means the blit landed on empty surface. 144 reads, negligible next to the blit itself.</summary>
    private static unsafe bool IsBlankFrame(IntPtr bits, int w, int h)
    {
        const int Samples = 12;
        var pixels = new Span<uint>((void*)bits, w * h);
        for (int sy = 0; sy < Samples; sy++)
        {
            int row = ((h - 1) * sy / (Samples - 1)) * w;
            for (int sx = 0; sx < Samples; sx++)
            {
                if ((pixels[row + (w - 1) * sx / (Samples - 1)] & 0x00FF_FFFFu) != 0)
                    return false;
            }
        }
        return true;
    }

    /// <summary>The window's rounded-corner radius in physical pixels (Win11 default ≈ 8 DIP, scaled
    /// by the window's DPI), clamped so it never exceeds half the capture.</summary>
    private static int CornerRadiusPx(HWND window, int w, int h)
    {
        uint dpi = PInvoke.GetDpiForWindow(window);
        if (dpi == 0)
            dpi = 96;
        int r = (int)Math.Round(8.0 * dpi / 96.0);
        return Math.Clamp(r, 0, Math.Min(w, h) / 2);
    }

    /// <summary>
    /// Builds the opaque window bitmap from the captured BGRX bits, then carves antialiased transparent
    /// rounded corners so the minimize warp follows the window's actual shape (not a square). BitBlt
    /// leaves the 4th byte unspecified, so alpha is set explicitly: 255 everywhere, tapering to 0 in the
    /// corner arcs.
    /// </summary>
    private static unsafe BitmapSource BuildWindowBitmap(IntPtr bits, int w, int h, int radius)
    {
        int stride = w * 4;

        // The CreateDIBSection memory is directly writable, so set the alpha and carve the corners IN PLACE
        // and copy once (WritePixels below) — rather than copying into a managed buffer, editing it, and
        // copying again. That halves the per-capture memory traffic (~10 MB saved for a 4K window), which
        // is the bulk of the build cost. BitBlt leaves the 4th (alpha) byte unspecified; OR the whole body
        // opaque (JIT-vectorizable over 32-bit pixels; alpha is the high byte in little-endian BGRA).
        var pixels = new Span<uint>((void*)bits, w * h);
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] |= 0xFF000000u;

        if (radius > 0)
        {
            byte* buf = (byte*)bits;
            CarveCorner(buf, stride, radius, 0, 0, radius, radius);                 // top-left
            CarveCorner(buf, stride, radius, w - radius, 0, w - radius, radius);     // top-right
            CarveCorner(buf, stride, radius, 0, h - radius, radius, h - radius);     // bottom-left
            CarveCorner(buf, stride, radius, w - radius, h - radius, w - radius, h - radius); // bottom-right
        }

        var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, w, h), (IntPtr)bits, stride * h, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Sets the alpha of a corner's r×r box to a quarter-disc centred at
    /// (<paramref name="cx"/>, <paramref name="cy"/>), with a 1px antialiased edge.</summary>
    private static unsafe void CarveCorner(byte* buf, int stride, int r, int x0, int y0, double cx, double cy)
    {
        for (int y = y0; y < y0 + r; y++)
        for (int x = x0; x < x0 + r; x++)
        {
            double dx = x + 0.5 - cx, dy = y + 0.5 - cy;
            double coverage = Math.Clamp(r - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0.0, 1.0);
            buf[y * stride + x * 4 + 3] = (byte)(coverage * 255.0 + 0.5);
        }
    }
}
