namespace CanAIRy;

// Pure rules shared by the normal and tray casting paths.
internal static class CastingPolicy
{
    public static bool CanChooseSource(bool presentationMode, bool desktop) => !presentationMode || !desktop;

    public static bool CanStart(bool discoveryCurrent, bool refreshing, bool active, bool updating,
        bool hasReceiver, bool hasSource) =>
        discoveryCurrent && !refreshing && !active && !updating && hasReceiver && hasSource;

    public static bool WasInterrupted(bool userStopped, bool sessionActive) => !userStopped && sessionActive;

    public static bool MatchesWindow(string? title, nint handle, string app,
        IEnumerable<(string? Title, nint Handle, string App)> windows)
    {
        if (string.IsNullOrWhiteSpace(title) || handle == 0) return false;
        var matches = windows.Where(w => string.Equals(w.Title, title, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 && matches[0].Handle == handle && matches[0].App == app;
    }

    public static string FormatRoundTrip(long? milliseconds) => milliseconds.HasValue
        ? $"Receiver RTT: {milliseconds.Value} ms (ICMP, not video latency)"
        : "Receiver RTT: no ping response (casting may still work)";
}
