using System.IO;

namespace CanAIRy;

internal static class ProductInfo
{
    public const string ShortName = "CanAIRy";
    public const string DisplayName = "CanAIRy — Wireless Display Casting";

    public static string AppDataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ShortName);

    public static string DataFile(string fileName)
    {
        Directory.CreateDirectory(AppDataFolder);
        var destination = Path.Combine(AppDataFolder, fileName);
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AirPlay Caster",
            fileName);

        if (!File.Exists(destination) && File.Exists(legacy))
            File.Copy(legacy, destination);

        return destination;
    }
}
