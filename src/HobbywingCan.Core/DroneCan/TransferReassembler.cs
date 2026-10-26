namespace HobbywingCan.Core.DroneCan;

/// <summary>Reassembles single- and multi-frame DroneCAN transfers from CAN frames (libcanard rules).</summary>
public sealed class TransferReassembler
{
    private const byte StartOfTransfer = 0x80;
    private const byte EndOfTransfer = 0x40;
    private const byte ToggleBit = 0x20;
    private const long SessionTimeoutMs = 2000;

    private sealed class Session
    {
        public byte TransferId;
        public bool NextToggle;
        public ushort Crc;
        public long StartedMs;
        public readonly List<byte> Payload = new();
    }

    private readonly Dictionary<uint, Session> _sessions = new();
    private readonly Func<bool, ushort, ulong?> _signatureLookup;

    /// <param name="signatureLookup">(isService, dataTypeId) → signature; multi-frame transfers of unknown types are dropped.</param>
    public TransferReassembler(Func<bool, ushort, ulong?>? signatureLookup = null)
    {
        _signatureLookup = signatureLookup ?? ((svc, id) => DataTypes.Find(svc, id)?.Signature);
    }

    public long CrcErrors { get; private set; }
    public long Dropped { get; private set; }

    public Transfer? Accept(CanFrame frame, long nowMs)
    {
        if (!frame.Extended || frame.Data.Length < 1)
        {
            return null;
        }

        uint id = frame.Id;
        byte tail = frame.Data[^1];
        bool sot = (tail & StartOfTransfer) != 0;
        bool eot = (tail & EndOfTransfer) != 0;
        bool toggle = (tail & ToggleBit) != 0;
        byte tid = (byte)(tail & 0x1F);
        var body = frame.Data.AsSpan(0, frame.Data.Length - 1);

        if (sot && eot)
        {
            return toggle ? null : Build(id, tid, body.ToArray());
        }

        uint key = id & 0x00FFFFFF;
        if (sot)
        {
            if (toggle || body.Length < 2)
            {
                Dropped++;
                return null;
            }
            var session = new Session
            {
                TransferId = tid,
                NextToggle = true,
                Crc = (ushort)(body[0] | body[1] << 8),
                StartedMs = nowMs,
            };
            session.Payload.AddRange(body[2..]);
            _sessions[key] = session;
            PurgeStale(nowMs);
            return null;
        }

        if (!_sessions.TryGetValue(key, out var s) || s.TransferId != tid || s.NextToggle != toggle ||
            nowMs - s.StartedMs > SessionTimeoutMs)
        {
            Dropped++;
            return null;
        }

        s.Payload.AddRange(body);
        s.NextToggle = !s.NextToggle;
        if (!eot)
        {
            return null;
        }

        _sessions.Remove(key);
        var payload = s.Payload.ToArray();
        var signature = _signatureLookup(CanId.IsService(id), CanId.DataTypeId(id));
        if (signature is null)
        {
            Dropped++;
            return null;
        }
        if (Crc16.ForTransfer(signature.Value, payload) != s.Crc)
        {
            CrcErrors++;
            return null;
        }
        return Build(id, tid, payload);
    }

    private static Transfer Build(uint id, byte tid, byte[] payload) =>
        new(CanId.Kind(id), CanId.DataTypeId(id), CanId.SourceNodeId(id), CanId.DestinationNodeId(id), tid, CanId.Priority(id), payload);

    private void PurgeStale(long nowMs)
    {
        if (_sessions.Count < 64)
        {
            return;
        }
        foreach (var key in _sessions.Where(kv => nowMs - kv.Value.StartedMs > SessionTimeoutMs).Select(kv => kv.Key).ToList())
        {
            _sessions.Remove(key);
        }
    }
}
