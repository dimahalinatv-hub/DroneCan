namespace HobbywingCan.Core.Mavlink;

/// <summary>
/// Incremental MAVLink v1/v2 stream parser. Only messages with a known CRC_EXTRA
/// (see <see cref="MavlinkIds"/>) are validated and emitted; everything else is skipped.
/// </summary>
public sealed class MavlinkParser
{
    private const byte StxV1 = 0xFE;
    private const byte StxV2 = 0xFD;
    private const int MaxFrameLength = 280;

    private readonly byte[] _buffer = new byte[4096];
    private int _count;

    public long PacketsParsed { get; private set; }
    public long CrcErrors { get; private set; }
    public long UnknownPackets { get; private set; }

    public IReadOnlyList<MavlinkPacket> Push(ReadOnlySpan<byte> data)
    {
        var result = new List<MavlinkPacket>();
        while (!data.IsEmpty)
        {
            int n = Math.Min(data.Length, _buffer.Length - _count);
            data[..n].CopyTo(_buffer.AsSpan(_count));
            _count += n;
            data = data[n..];
            Drain(result);
        }
        return result;
    }

    private void Drain(List<MavlinkPacket> output)
    {
        int pos = 0;
        while (true)
        {
            int start = IndexOfStx(pos);
            if (start < 0)
            {
                pos = _count;
                break;
            }
            pos = start;
            int consumed = TryParseAt(pos, out var packet);
            if (consumed == 0)
            {
                // need more data
                break;
            }
            if (consumed < 0)
            {
                // not a valid frame at this position, resync on next byte
                pos++;
                continue;
            }
            if (packet != null)
            {
                output.Add(packet);
            }
            pos += consumed;
        }

        if (pos > 0)
        {
            Buffer.BlockCopy(_buffer, pos, _buffer, 0, _count - pos);
            _count -= pos;
        }
        if (_count == _buffer.Length)
        {
            // should never happen with valid frames, avoid getting stuck
            _count = 0;
        }
    }

    private int IndexOfStx(int from)
    {
        for (int i = from; i < _count; i++)
        {
            if (_buffer[i] == StxV1 || _buffer[i] == StxV2)
            {
                return i;
            }
        }
        return -1;
    }

    /// <returns>bytes consumed, 0 if more data is needed, -1 if the frame is invalid.</returns>
    private int TryParseAt(int pos, out MavlinkPacket? packet)
    {
        packet = null;
        int available = _count - pos;
        bool v2 = _buffer[pos] == StxV2;
        int headerLength = v2 ? 10 : 6;
        if (available < headerLength)
        {
            return 0;
        }

        if (v2 && (_buffer[pos + 2] & ~0x01) != 0)
        {
            // only the "signed" incompat flag exists; anything else means this is not a real STX
            return -1;
        }

        int payloadLength = _buffer[pos + 1];
        int signatureLength = v2 && (_buffer[pos + 2] & 0x01) != 0 ? 13 : 0;
        int frameLength = headerLength + payloadLength + 2 + signatureLength;
        if (frameLength > MaxFrameLength)
        {
            return -1;
        }
        if (available < frameLength)
        {
            return 0;
        }

        byte sysId, compId;
        uint msgId;
        if (v2)
        {
            sysId = _buffer[pos + 5];
            compId = _buffer[pos + 6];
            msgId = (uint)(_buffer[pos + 7] | _buffer[pos + 8] << 8 | _buffer[pos + 9] << 16);
        }
        else
        {
            sysId = _buffer[pos + 3];
            compId = _buffer[pos + 4];
            msgId = _buffer[pos + 5];
        }

        if (!MavlinkIds.TryGetCrcExtra(msgId, out byte crcExtra))
        {
            UnknownPackets++;
            return frameLength;
        }

        var crcSpan = _buffer.AsSpan(pos + 1, headerLength - 1 + payloadLength);
        ushort crc = X25Crc.Accumulate(crcSpan);
        crc = X25Crc.Accumulate(crcExtra, crc);
        int crcPos = pos + headerLength + payloadLength;
        ushort received = (ushort)(_buffer[crcPos] | _buffer[crcPos + 1] << 8);
        if (crc != received)
        {
            CrcErrors++;
            return -1;
        }

        var payload = _buffer.AsSpan(pos + headerLength, payloadLength).ToArray();
        packet = new MavlinkPacket(sysId, compId, msgId, payload);
        PacketsParsed++;
        return frameLength;
    }
}
