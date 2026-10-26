namespace HobbywingCan.Core.DroneCan;

/// <summary>Writes DSDL-serialized fields with the libcanard canardEncodeScalar bit layout.</summary>
public sealed class BitWriter
{
    private readonly List<byte> _data = new();
    private int _bitOffset;

    public void WriteUnsigned(ulong value, int bitLength)
    {
        if (bitLength is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLength));
        }

        Span<byte> bytes = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            bytes[i] = (byte)(value >> (8 * i));
        }
        int rem = bitLength % 8;
        if (rem != 0)
        {
            bytes[bitLength / 8] <<= 8 - rem;
        }

        for (int i = 0; i < bitLength; i++)
        {
            int bit = (bytes[i / 8] >> (7 - i % 8)) & 1;
            int dst = _bitOffset + i;
            while (_data.Count <= dst / 8)
            {
                _data.Add(0);
            }
            if (bit != 0)
            {
                _data[dst / 8] |= (byte)(0x80 >> (dst % 8));
            }
        }
        _bitOffset += bitLength;
    }

    public void WriteSigned(long value, int bitLength) => WriteUnsigned((ulong)value, bitLength);

    public void WriteU8(byte value) => WriteUnsigned(value, 8);

    public void WriteU16(ushort value) => WriteUnsigned(value, 16);

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            WriteU8(b);
        }
    }

    public byte[] ToArray()
    {
        int byteLength = (_bitOffset + 7) / 8;
        while (_data.Count < byteLength)
        {
            _data.Add(0);
        }
        return _data.GetRange(0, byteLength).ToArray();
    }
}
