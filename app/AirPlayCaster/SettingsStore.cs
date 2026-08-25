using System.Text.Json;
using System.IO;
using Microsoft.Win32;

namespace AirPlayCaster;

internal sealed class AppSettings
{
    public string? SelectedDeviceId { get; set; }
    public int FramesPerSecond { get; set; } = 30;
    public int BitrateKbps { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool StartInTray { get; set; } = true;
    public List<Receiver> KnownReceivers { get; set; } = [];
}

internal static class SettingsStore
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AirPlay Caster";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AirPlay Caster", "settings.json");

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
            return key?.GetValue(RunValue) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (value)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --tray", RegistryValueKind.String);
            else
                key.DeleteValue(RunValue, false);
        }
    }
}
