using System.Text.Json;
using System.IO;
using Microsoft.Win32;

namespace CanAIRy;

internal sealed class AppSettings
{
    public string? SelectedDeviceId { get; set; }
    public int FramesPerSecond { get; set; } = 30;
    public int BitrateKbps { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool StartInTray { get; set; } = true;
    public bool AutomaticUpdates { get; set; } = true;
    public string? LastUpdateAttemptVersion { get; set; }
    public List<Receiver> KnownReceivers { get; set; } = [];
}

internal static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "CanAIRy";
    private const string LegacyRunValue = "AirPlay Caster";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => ProductInfo.DataFile("settings.json");

    public static bool HasSettings => File.Exists(SettingsPath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }

    public static bool LaunchAtSignIn
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            if (key?.GetValue(RunValue) is string) return true;
            if (key?.GetValue(LegacyRunValue) is not string) return false;

            using var writableKey = Registry.CurrentUser.CreateSubKey(RunKey, true);
            writableKey.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray", RegistryValueKind.String);
            writableKey.DeleteValue(LegacyRunValue, false);
            return true;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray", RegistryValueKind.String);
            else
                key.DeleteValue(RunValue, false);
            key.DeleteValue(LegacyRunValue, false);
        }
    }
}
