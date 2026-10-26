namespace HobbywingCan.Core.DroneCan;

/// <summary>
/// Reads DSDL-serialized fields with the same bit layout as libcanard's canardDecodeScalar:
/// bits are taken MSB-first from the stream, multi-byte values are little-endian.
/// Reading past the end yields zeros (implicit zero extension).
/// </summary>
public sealed class BitReader(byte[] data)
{
    private readonly byte[] _data = data;

    public int BitOffset { get; private set; }

    public int RemainingBits => Math.Max(0, _data.Length * 8 - BitOffset);

    public ulong ReadUnsigned(int bitLength)
    {
        if (bitLength is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bitLength));
        }

        Span<byte> bytes = stackalloc byte[8];
        bytes.Clear();
        for (int i = 0; i < bitLength; i++)
        {
            int src = BitOffset + i;
            int bit = src / 8 < _data.Length ? (_data[src / 8] >> (7 - src % 8)) & 1 : 0;
            if (bit != 0)
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }
        BitOffset += bitLength;

        int rem = bitLength % 8;
        if (rem != 0)
        {
            bytes[bitLength / 8] >>= 8 - rem;
        }

        ulong value = 0;
        for (int i = 0; i < 8; i++)
        {
            value |= (ulong)bytes[i] << (8 * i);
        }
        return value;
    }

    public long ReadSigned(int bitLength)
    {
        ulong raw = ReadUnsigned(bitLength);
        if (bitLength < 64 && (raw & (1UL << (bitLength - 1))) != 0)
        {
            raw |= ulong.MaxValue << bitLength;
        }
        return (long)raw;
    }

    public bool ReadBool() => ReadUnsigned(1) != 0;

    public byte ReadU8() => (byte)ReadUnsigned(8);

    public ushort ReadU16() => (ushort)ReadUnsigned(16);

    public uint ReadU32() => (uint)ReadUnsigned(32);

    public float ReadFloat16() => (float)BitConverter.UInt16BitsToHalf((ushort)ReadUnsigned(16));

    public float ReadFloat32() => BitConverter.UInt32BitsToSingle((uint)ReadUnsigned(32));

    public void Skip(int bitLength) => BitOffset += bitLength;

    public byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = ReadU8();
        }
        return result;
    }

    /// <summary>Dynamic uint8 array with an explicit length prefix (non-tail position).</summary>
    public byte[] ReadDynamicBytes(int lengthPrefixBits) => ReadBytes((int)ReadUnsigned(lengthPrefixBits));

    /// <summary>Dynamic uint8 array in tail position (tail array optimization: no length prefix).</summary>
    public byte[] ReadTailBytes(int maxLength) => ReadBytes(Math.Min(maxLength, RemainingBits / 8));
}
