using ScanBridge.Agent;
using ScanBridge.Protocol;
using Xunit;

namespace ScanBridge.Tests.Unit;

/// <summary>
/// Tests for the list a remote session is offered.
///
/// The names are synthetic but structurally exact: same lengths, same "WIA-" prefix, and the
/// same point of truncation as the real pairs this was built against. TWAIN carries product
/// names in a TW_STR32 and cuts them off, so a shimmed device's TWAIN name is only ever a
/// prefix of its WIA name, never equal to it — which is the whole reason a naive equality
/// check fails to collapse the duplicates. Shortening these strings would silently stop the
/// tests exercising that.
/// </summary>
public sealed class ScannerListTests
{
    private static ScannerInfo Wia(string name, string id) => new(
        id, name, "Microsoft", ScannerInterface.Wia, ScannerStatus.Ready,
        ScannerFeatures.None, Is32BitOnly: false);

    private static ScannerInfo Twain(string name, string id) => new(
        id, name, "Microsoft", ScannerInterface.Twain, ScannerStatus.Ready,
        ScannerFeatures.None, Is32BitOnly: false);

    [Fact]
    public void CollapsesTheWiaToTwainShimEvenThoughTwainTruncatedTheName()
    {
        var raw = new[]
        {
            Wia("Contoso IJ-48200W [a1b2c3d4e5f6]", "wia:{guid}\\0003"),
            // TW_STR32 cuts the name off at 32 characters, so this is never equal to the WIA
            // name above — only ever a prefix of it.
            Twain("WIA-Contoso IJ-48200W [a1b2c3d4e5", "twain:WIA-Contoso IJ-48200W"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.CollapseWiaShimDuplicates(raw);

        ScannerInfo kept = Assert.Single(result);
        Assert.Equal(ScannerInterface.Wia, kept.Interface);
    }

    [Fact]
    public void ThreeDevicesEnumeratedTwiceEachCollapseToThree()
    {
        var raw = new[]
        {
            Wia("Contoso IJ-48200W [a1b2c3d4e5f6]", "wia:a"),
            Wia("Contoso IJ-48200W [a1b2c3d4f7a8]", "wia:b"),
            Wia("Contoso IJ-48100W [9f8e7d6c5b4a]", "wia:c"),
            Twain("WIA-Contoso IJ-48200W [a1b2c3d4e5", "twain:a"),
            Twain("WIA-Contoso IJ-48200W [a1b2c3d4f7", "twain:b"),
            Twain("WIA-Contoso IJ-48100W [9f8e7d6c5b", "twain:c"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.CollapseWiaShimDuplicates(raw);

        Assert.Equal(3, result.Count);
        Assert.All(result, s => Assert.Equal(ScannerInterface.Wia, s.Interface));
    }

    [Fact]
    public void KeepsARealVendorTwainDriver()
    {
        // A vendor's own TWAIN driver is not named "WIA-anything" and is often better than the
        // WIA path, so it must survive. Losing it would silently downgrade the user.
        var raw = new[]
        {
            Wia("Contoso IJ-48200W [a1b2c3d4e5f6]", "wia:a"),
            Twain("Contoso IJ-48200W", "twain:shim"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.CollapseWiaShimDuplicates(raw);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void KeepsAShimEntryWhoseWiaDeviceIsGone()
    {
        // The 32-bit and 64-bit hosts do not always see the same devices. A shim entry with no
        // matching WIA device is the only way to reach that scanner, so it stays.
        var raw = new[]
        {
            Wia("Contoso IJ-48100W [9f8e7d6c5b4a]", "wia:c"),
            Twain("WIA-Canon LiDE 300", "twain:canon"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.CollapseWiaShimDuplicates(raw);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void PreferDefaultMovesTheChosenScannerToTheFront()
    {
        // Position is the selection mechanism: the data source binds to index 0.
        var scanners = new[]
        {
            Wia("Contoso IJ-48200W", "wia:a"),
            Wia("Contoso IJ-48100W", "wia:b"),
            Wia("Canon LiDE 300", "wia:c"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.PreferDefault(scanners, "wia:c");

        Assert.Equal("wia:c", result[0].Id);
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void PreferDefaultKeepsTheOrderOfTheRest()
    {
        var scanners = new[]
        {
            Wia("A", "wia:a"),
            Wia("B", "wia:b"),
            Wia("C", "wia:c"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.PreferDefault(scanners, "wia:c");

        Assert.Equal(new[] { "wia:c", "wia:a", "wia:b" }, result.Select(s => s.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("wia:not-here")]
    public void PreferDefaultLeavesTheListAloneWhenTheChoiceIsUnsetOrGone(string? defaultId)
    {
        // A scanner that has been unplugged must not blank the list or reorder it oddly; the
        // first one found is the right answer for the overwhelmingly common single-scanner PC.
        var scanners = new[] { Wia("A", "wia:a"), Wia("B", "wia:b") };

        IReadOnlyList<ScannerInfo> result = ScannerList.PreferDefault(scanners, defaultId);

        Assert.Equal(new[] { "wia:a", "wia:b" }, result.Select(s => s.Id));
    }

    [Fact]
    public void ArrangeCollapsesThenOrders()
    {
        // Order matters: collapsing after ordering could promote a shim entry that is about to
        // be removed, leaving the wrong scanner first.
        var raw = new[]
        {
            Wia("Contoso IJ-48200W [a1b2c3d4e5f6]", "wia:a"),
            Twain("WIA-Contoso IJ-48200W [a1b2c3d4e5", "twain:a"),
            Wia("Contoso IJ-48100W [9f8e7d6c5b4a]", "wia:c"),
            Twain("WIA-Contoso IJ-48100W [9f8e7d6c5b", "twain:c"),
        };

        IReadOnlyList<ScannerInfo> result = ScannerList.Arrange(raw, "wia:c");

        Assert.Equal(2, result.Count);
        Assert.Equal("wia:c", result[0].Id);
        Assert.Equal("wia:a", result[1].Id);
    }

    [Fact]
    public void AnEmptyListIsNotAnError()
    {
        Assert.Empty(ScannerList.Arrange(Array.Empty<ScannerInfo>(), "wia:a"));
    }
}
