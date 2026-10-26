namespace HobbywingCan.Core.Mavlink;

/// <summary>CRC-16/MCRF4XX (X.25) as used by MAVLink.</summary>
public static class X25Crc
{
    public const ushort Seed = 0xFFFF;

    public static ushort Accumulate(byte value, ushort crc)
    {
        byte tmp = (byte)(value ^ (byte)(crc & 0xFF));
        tmp ^= (byte)(tmp << 4);
        return (ushort)((crc >> 8) ^ (tmp << 8) ^ (tmp << 3) ^ (tmp >> 4));
    }

    public static ushort Accumulate(ReadOnlySpan<byte> data, ushort crc = Seed)
    {
        foreach (var b in data)
        {
            crc = Accumulate(b, crc);
        }
        return crc;
    }
}
