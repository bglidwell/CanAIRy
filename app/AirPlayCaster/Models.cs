namespace AirPlayCaster;

public sealed class Receiver
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; } = 7000;
    public string DeviceId { get; set; } = "";
    public string DisplayName => string.IsNullOrWhiteSpace(Model)
        ? $"{Name}  ·  {Ip}"
        : $"{Name}  ·  {Model}";
}

public sealed class CaptureSource
{
    public string Name { get; init; } = "";
    public string? WindowTitle { get; init; }
    public bool IsDesktop => WindowTitle is null;
    public override string ToString() => Name;
}

public sealed record StreamOptions(
    Receiver Receiver,
    CaptureSource Source,
    int FramesPerSecond,
    int BitrateKbps,
    bool ShowCursor,
    bool ForcePair);
