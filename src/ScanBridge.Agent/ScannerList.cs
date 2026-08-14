using ScanBridge.Protocol;

namespace ScanBridge.Agent;

/// <summary>
/// Turns raw enumeration results into the list a remote session should see.
///
/// Two things have to happen between "every driver on this PC" and "the scanners the user
/// meant": the same physical device reported by two stacks has to be collapsed into one entry,
/// and the user's chosen scanner has to end up first.
///
/// Pure functions, deliberately: this is the logic most likely to be wrong in a way nobody
/// notices, so it is separated from the enumeration plumbing and tested directly.
/// </summary>
public static class ScannerList
{
    /// <summary>
    /// Prefix Windows' WIA-to-TWAIN compatibility shim puts on the sources it synthesises.
    /// </summary>
    private const string ShimPrefix = "WIA-";

    /// <summary>
    /// Removes the duplicate entries Windows' own WIA-to-TWAIN shim creates.
    ///
    /// Every WIA scanner on a machine is re-presented as a TWAIN source named
    /// "WIA-&lt;device name&gt;" by <c>wiatwain.ds</c>, so a PC with three WIA scanners enumerates
    /// six. They are not different scanners and offering both is worse than useless: the user
    /// cannot tell which is which, and the shim is the poorer of the two — it is a wrapper
    /// around the same WIA driver, and the one that reports a scanner bed of -32768 x -19661
    /// inches while returning success.
    ///
    /// The shim's name is matched by prefix rather than equality because TWAIN product names
    /// are TW_STR32 and are truncated at 32 characters, so "WIA-Contoso IJ-48200W
    /// [a1b2c3d4e5f6]" arrives as "WIA-Contoso IJ-48200W [a1b2c3d4e5" — never equal to the WIA
    /// name, always a prefix of it.
    ///
    /// A real vendor TWAIN driver is untouched: those are not named "WIA-anything", and where
    /// a device genuinely offers both stacks the user still sees both and can pick.
    /// </summary>
    public static IReadOnlyList<ScannerInfo> CollapseWiaShimDuplicates(
        IReadOnlyList<ScannerInfo> scanners)
    {
        ArgumentNullException.ThrowIfNull(scanners);

        var wiaNames = scanners
            .Where(s => s.Interface == ScannerInterface.Wia)
            .Select(s => s.Name)
            .ToList();

        if (wiaNames.Count == 0) return scanners;

        return scanners.Where(scanner =>
        {
            if (scanner.Interface != ScannerInterface.Twain) return true;
            if (!scanner.Name.StartsWith(ShimPrefix, StringComparison.OrdinalIgnoreCase)) return true;

            string underlying = scanner.Name[ShimPrefix.Length..];
            if (underlying.Length == 0) return true;

            // Truncation means the shim's name is a prefix of the WIA device's name.
            return !wiaNames.Any(wia => wia.StartsWith(underlying, StringComparison.OrdinalIgnoreCase));
        }).ToList();
    }

    /// <summary>
    /// Moves the user's chosen scanner to the front, leaving everything else in order.
    ///
    /// Position is not cosmetic here. The virtual data source on the server represents exactly
    /// one scanner and binds to index 0, so this ordering *is* the mechanism by which a user
    /// with several scanners chooses which one their remote applications get.
    ///
    /// An unset or no-longer-present id leaves the order alone, which means "whatever was
    /// found first" — the right behaviour for the common case of a single scanner.
    /// </summary>
    public static IReadOnlyList<ScannerInfo> PreferDefault(
        IReadOnlyList<ScannerInfo> scanners, string? defaultScannerId)
    {
        ArgumentNullException.ThrowIfNull(scanners);

        if (string.IsNullOrEmpty(defaultScannerId) || scanners.Count < 2) return scanners;

        int index = -1;
        for (int i = 0; i < scanners.Count; i++)
        {
            if (string.Equals(scanners[i].Id, defaultScannerId, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index <= 0) return scanners;   // absent, or already first

        var ordered = new List<ScannerInfo>(scanners.Count) { scanners[index] };
        for (int i = 0; i < scanners.Count; i++)
        {
            if (i != index) ordered.Add(scanners[i]);
        }
        return ordered;
    }

    /// <summary>Both steps, in the order they must happen.</summary>
    public static IReadOnlyList<ScannerInfo> Arrange(
        IReadOnlyList<ScannerInfo> scanners, string? defaultScannerId)
        => PreferDefault(CollapseWiaShimDuplicates(scanners), defaultScannerId);
}

/// <summary>
/// One Remote Desktop connection that can reach this PC's scanner.
/// </summary>
/// <param name="Id">Sequence number, so the newest link is obvious.</param>
/// <param name="PeerName">Machine name the plugin reported when it connected.</param>
/// <param name="ConnectedAt">When the link came up.</param>
public sealed record RemoteLink(int Id, string PeerName, DateTime ConnectedAt)
{
    public string Connected => ConnectedAt.ToString("HH:mm:ss");

    /// <summary>
    /// How long it has been up, rounded to something a person reads at a glance. Recomputed on
    /// each read rather than stored, so a window left open does not show a stale age.
    /// </summary>
    public string Age
    {
        get
        {
            TimeSpan up = DateTime.Now - ConnectedAt;
            if (up.TotalMinutes < 1) return $"{(int)up.TotalSeconds}s";
            if (up.TotalHours < 1) return $"{(int)up.TotalMinutes} min";
            return $"{(int)up.TotalHours} h {up.Minutes} min";
        }
    }
}
