namespace HobbywingCan.Core.DroneCan;

/// <summary>CRC-16-CCITT-FALSE used for DroneCAN multi-frame transfers.</summary>
public static class Crc16
{
    public static ushort Add(ushort crc, byte value)
    {
        crc ^= (ushort)(value << 8);
        for (int i = 0; i < 8; i++)
        {
            crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>CRC of a transfer: seeded with the data type signature (little-endian), then the payload.</summary>
    public static ushort ForTransfer(ulong signature, ReadOnlySpan<byte> payload)
    {
        ushort crc = 0xFFFF;
        for (int shift = 0; shift < 64; shift += 8)
        {
            crc = Add(crc, (byte)(signature >> shift));
        }
        foreach (var b in payload)
        {
            crc = Add(crc, b);
        }
        return crc;
    }
}
