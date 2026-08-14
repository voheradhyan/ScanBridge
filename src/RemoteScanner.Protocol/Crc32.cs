namespace RemoteScanner.Protocol;

/// <summary>
/// CRC-32 (IEEE 802.3, reflected, polynomial 0xEDB88320).
/// Used to verify each page survived the channel intact. Not a security primitive —
/// integrity against a hostile peer is the HMAC handshake's job, this catches corruption.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint polynomial = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? (value >> 1) ^ polynomial : value >> 1;
            table[i] = value;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Append(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;

    /// <summary>Running state for chunked input. Seed with <see cref="Seed"/>, finish with <see cref="Finish"/>.</summary>
    public const uint Seed = 0xFFFFFFFFu;

    public static uint Append(uint state, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            state = (state >> 8) ^ Table[(state ^ b) & 0xFF];
        return state;
    }

    public static uint Finish(uint state) => state ^ 0xFFFFFFFFu;
}
