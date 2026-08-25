using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace CanAIRy;

internal static class WindowCatalog
{
    private const int DwmCloaked = 14;
    private const int DwmExtendedFrameBounds = 9;
    private const uint PrintWindowRenderFullContent = 2;
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr handle, IntPtr deviceContext, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out NativeRect value, int size);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    public static IReadOnlyList<CaptureSource> GetSources()
    {
        var sources = new List<CaptureSource>
        {
            new() { Name = "Entire desktop", AppName = "All screens and apps" }
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownProcessId = Environment.ProcessId;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            if (DwmGetWindowAttribute(handle, DwmCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var title = new StringBuilder(1024);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            var windowTitle = title.ToString().Trim();
            if (windowTitle.Length == 0 || !seen.Add(windowTitle)) return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == ownProcessId) return true;
            var processName = "App";
            System.Windows.Media.ImageSource? appIcon = null;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
                appIcon = LoadAppIcon(process);
            }
            catch { }

            sources.Add(new CaptureSource
            {
                Name = $"{windowTitle}  ·  {processName}",
                WindowTitle = windowTitle,
                AppName = processName,
                WindowHandle = handle,
                AppIcon = appIcon
            });
            return true;
        }, IntPtr.Zero);

        return sources.Skip(1)
            .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(sources[0])
            .ToArray();
    }

    public static BitmapSource? CaptureThumbnail(CaptureSource source, int maxWidth = 720, int maxHeight = 405)
    {
        try
        {
            return source.IsDesktop
                ? CaptureDesktop(maxWidth, maxHeight)
                : CaptureWindow(source.WindowHandle, maxWidth, maxHeight);
        }
        catch
        {
            return null;
        }
    }

    private static System.Windows.Media.ImageSource? LoadAppIcon(Process process)
    {
        try
        {
            var executable = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable)) return null;
            using var icon = Drawing.Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return null;
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? CaptureDesktop(int maxWidth, int maxHeight)
    {
        var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;
        using var bitmap = new Drawing.Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, Drawing.CopyPixelOperation.SourceCopy);
        return ToBitmapSource(bitmap, maxWidth, maxHeight);
    }

    private static BitmapSource? CaptureWindow(IntPtr handle, int maxWidth, int maxHeight)
    {
        if (handle == IntPtr.Zero || !IsWindowVisible(handle)) return null;
        if (DwmGetWindowAttribute(handle, DwmExtendedFrameBounds, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0 &&
            !GetWindowRect(handle, out rect)) return null;
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 1 || height <= 1 || width > 12000 || height > 12000) return null;

        using var bitmap = new Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        var deviceContext = graphics.GetHdc();
        bool captured;
        try { captured = PrintWindow(handle, deviceContext, PrintWindowRenderFullContent); }
        finally { graphics.ReleaseHdc(deviceContext); }
        return captured ? ToBitmapSource(bitmap, maxWidth, maxHeight) : null;
    }

    private static BitmapSource ToBitmapSource(Drawing.Bitmap source, int maxWidth, int maxHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)maxWidth / source.Width, (double)maxHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var resized = new Drawing.Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Drawing.Graphics.FromImage(resized))
        {
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        var nativeBitmap = resized.GetHbitmap();
        try
        {
            var image = Imaging.CreateBitmapSourceFromHBitmap(
                nativeBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DeleteObject(nativeBitmap);
        }
    }
}
