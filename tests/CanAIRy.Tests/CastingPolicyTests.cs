using Xunit;

namespace CanAIRy.Tests;

public sealed class CastingPolicyTests
{
    [Fact]
    public void PresentationRejectsDesktop()
    {
        Assert.False(CastingPolicy.CanChooseSource(true, true));
        Assert.True(CastingPolicy.CanChooseSource(true, false));
        Assert.True(CastingPolicy.CanChooseSource(false, true));
    }

    [Fact]
    public void StartRequiresFreshDiscoveryAndBothSelections()
    {
        Assert.True(CastingPolicy.CanStart(true, false, false, false, true, true));
        Assert.False(CastingPolicy.CanStart(false, false, false, false, true, true));
        Assert.False(CastingPolicy.CanStart(true, true, false, false, true, true));
        Assert.False(CastingPolicy.CanStart(true, false, true, false, true, true));
        Assert.False(CastingPolicy.CanStart(true, false, false, true, true, true));
        Assert.False(CastingPolicy.CanStart(true, false, false, false, false, true));
        Assert.False(CastingPolicy.CanStart(true, false, false, false, true, false));
    }

    [Fact]
    public void ReconnectNeverTreatsUserStopAsInterruption()
    {
        Assert.True(CastingPolicy.WasInterrupted(false, true));
        Assert.False(CastingPolicy.WasInterrupted(true, true));
        Assert.False(CastingPolicy.WasInterrupted(false, false));
    }

    [Fact]
    public void RejectsReusedHandlesAndAmbiguousWindowTitles()
    {
        (string? Title, nint Handle, string App)[] windows = [("Notes", 10, "notepad")];
        Assert.True(CastingPolicy.MatchesWindow("Notes", 10, "notepad", windows));
        Assert.False(CastingPolicy.MatchesWindow("Notes", 11, "notepad", windows));
        Assert.False(CastingPolicy.MatchesWindow("Notes", 10, "browser", windows));
        Assert.False(CastingPolicy.MatchesWindow("Notes", 10, "notepad", [..windows, ("notes", 12, "browser")]));
        Assert.False(CastingPolicy.MatchesWindow("Notes", 10, "notepad", []));
    }

    [Fact]
    public void RoundTripDoesNotClaimVideoLatency()
    {
        Assert.Contains("not video latency", CastingPolicy.FormatRoundTrip(4));
        Assert.Contains("casting may still work", CastingPolicy.FormatRoundTrip(null));
    }
}
