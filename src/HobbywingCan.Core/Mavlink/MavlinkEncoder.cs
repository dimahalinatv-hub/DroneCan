namespace HobbywingCan.Core.Mavlink;

/// <summary>Builds MAVLink 2 frames (unsigned, with trailing-zero payload truncation).</summary>
public sealed class MavlinkEncoder(byte systemId, byte componentId)
{
    private byte _sequence;

    public byte SystemId { get; } = systemId;
    public byte ComponentId { get; } = componentId;

    public byte[] Encode(uint messageId, ReadOnlySpan<byte> payload)
    {
        if (!MavlinkIds.TryGetCrcExtra(messageId, out byte crcExtra))
        {
            throw new ArgumentException($"Unknown MAVLink message {messageId}", nameof(messageId));
        }

        int length = payload.Length;
        while (length > 1 && payload[length - 1] == 0)
        {
            length--;
        }

        var frame = new byte[10 + length + 2];
        frame[0] = 0xFD;
        frame[1] = (byte)length;
        frame[2] = 0; // incompat flags
        frame[3] = 0; // compat flags
        frame[4] = _sequence++;
        frame[5] = SystemId;
        frame[6] = ComponentId;
        frame[7] = (byte)messageId;
        frame[8] = (byte)(messageId >> 8);
        frame[9] = (byte)(messageId >> 16);
        payload[..length].CopyTo(frame.AsSpan(10));

        ushort crc = X25Crc.Accumulate(frame.AsSpan(1, 9 + length));
        crc = X25Crc.Accumulate(crcExtra, crc);
        frame[10 + length] = (byte)crc;
        frame[11 + length] = (byte)(crc >> 8);
        return frame;
    }
}
