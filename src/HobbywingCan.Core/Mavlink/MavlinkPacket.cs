using System.Buffers.Binary;

namespace HobbywingCan.Core.Mavlink;

/// <summary>
/// A received MAVLink packet. MAVLink 2 truncates trailing zero bytes of the payload,
/// so all accessors treat bytes past the end of <see cref="Payload"/> as zero.
/// </summary>
public sealed class MavlinkPacket(byte systemId, byte componentId, uint messageId, byte[] payload)
{
    public byte SystemId { get; } = systemId;
    public byte ComponentId { get; } = componentId;
    public uint MessageId { get; } = messageId;
    public byte[] Payload { get; } = payload;

    public byte U8(int offset) => offset < Payload.Length ? Payload[offset] : (byte)0;

    public ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Padded(offset, 2));

    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Padded(offset, 4));

    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Padded(offset, 4));

    public byte[] Bytes(int offset, int length) => Padded(offset, length).ToArray();

    private ReadOnlySpan<byte> Padded(int offset, int length)
    {
        if (offset + length <= Payload.Length)
        {
            return Payload.AsSpan(offset, length);
        }
        var buffer = new byte[length];
        if (offset < Payload.Length)
        {
            Payload.AsSpan(offset).CopyTo(buffer);
        }
        return buffer;
    }
}
