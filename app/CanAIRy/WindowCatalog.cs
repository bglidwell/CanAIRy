using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CanAIRy;

internal static class WindowCatalog
{
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);

    public static IReadOnlyList<CaptureSource> GetSources()
    {
        var sources = new List<CaptureSource>
        {
            new() { Name = "Entire desktop" }
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownProcessId = Environment.ProcessId;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            if (DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;

            var title = new StringBuilder(1024);
            if (GetWindowText(handle, title, title.Capacity) == 0) return true;
            var windowTitle = title.ToString().Trim();
            if (windowTitle.Length == 0 || !seen.Add(windowTitle)) return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == ownProcessId) return true;
            string processName;
            try { processName = Process.GetProcessById((int)processId).ProcessName; }
            catch { processName = "App"; }

            sources.Add(new CaptureSource
            {
                Name = $"{windowTitle}  ·  {processName}",
                WindowTitle = windowTitle
            });
            return true;
        }, IntPtr.Zero);

        return sources.Skip(1)
            .OrderBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .Prepend(sources[0])
            .ToArray();
    }
}
