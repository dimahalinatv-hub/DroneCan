namespace HobbywingCan.Core.DroneCan;

/// <summary>Splits a transfer payload into classic-CAN DroneCAN frames.</summary>
public static class TransferEncoder
{
    public static IReadOnlyList<CanFrame> Encode(uint canId, byte transferId, ReadOnlySpan<byte> payload, ulong signature)
    {
        var frames = new List<CanFrame>();
        byte tid = (byte)(transferId & 0x1F);

        if (payload.Length <= 7)
        {
            var data = new byte[payload.Length + 1];
            payload.CopyTo(data);
            data[^1] = (byte)(0xC0 | tid);
            frames.Add(new CanFrame(canId, data));
            return frames;
        }

        ushort crc = Crc16.ForTransfer(signature, payload);
        var stream = new byte[payload.Length + 2];
        stream[0] = (byte)crc;
        stream[1] = (byte)(crc >> 8);
        payload.CopyTo(stream.AsSpan(2));

        bool toggle = false;
        for (int offset = 0; offset < stream.Length; offset += 7)
        {
            int n = Math.Min(7, stream.Length - offset);
            var data = new byte[n + 1];
            Array.Copy(stream, offset, data, 0, n);
            byte tail = tid;
            if (offset == 0) tail |= 0x80;
            if (offset + n >= stream.Length) tail |= 0x40;
            if (toggle) tail |= 0x20;
            data[^1] = tail;
            frames.Add(new CanFrame(canId, data));
            toggle = !toggle;
        }
        return frames;
    }
}
