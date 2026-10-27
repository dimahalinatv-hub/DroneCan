using System.Buffers.Binary;
using System.Text;
using System.Threading.Channels;
using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Messages;

namespace HobbywingCan.Core.Simulation;

/// <summary>
/// A stream that behaves like ArduPilot's MAVLink port with CAN forwarding and a few Hobbywing ESCs on CAN1.
/// Used by tests and by the app's "SIMULATOR" port to exercise everything without hardware.
/// </summary>
public sealed class SimulatedAutopilot : Stream
{
    public const string PortName = "SIMULATOR";
    public const byte AutopilotNodeId = 10;

    private readonly Channel<byte[]> _toApp = Channel.CreateUnbounded<byte[]>();
    private readonly MavlinkEncoder _encoder = new(1, 1);
    private readonly MavlinkParser _parser = new();
    private readonly TransferReassembler _reassembler = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<SimEsc> _escs;
    private byte[] _pending = [];
    private byte _tid;
    private bool _forwarding;

    /// <summary>Settings of one simulated ESC, changed through the com.hobbywing.esc.Set* services.</summary>
    public sealed class SimEsc
    {
        public byte NodeId { get; set; }
        public byte ThrottleChannel { get; set; }
        public bool CounterClockwise { get; set; }
        public bool PwmThrottle { get; set; }
        public byte LedColor { get; set; } = 2;
        public byte LedBlink { get; set; }
        public byte BaudCode { get; set; }
        /// <summary>Transfer id of this ESC's next GetEscID reply (ESCs started together stay in lockstep).</summary>
        public byte EscIdTransferId { get; set; }
        public Dictionary<ushort, byte> RateCodes { get; } = new()
        {
            [DataTypes.HwStatusMsg1.Id] = 5, // 50 Hz
            [DataTypes.HwStatusMsg2.Id] = 7, // 10 Hz
            [DataTypes.HwStatusMsg3.Id] = 7, // 10 Hz
        };
    }

    /// <param name="escNodeIds">ESC node ids; ESC i gets throttle channel i+1.</param>
    /// <param name="autoRun">Publish heartbeats and telemetry periodically (false for deterministic tests).</param>
    /// <param name="setIdIgnoresDestination">
    /// Mimic Hobbywing X8 G2: every ESC applies (and answers) SetID, whichever node it is addressed to.
    /// </param>
    public SimulatedAutopilot(byte[] escNodeIds, bool autoRun, bool setIdIgnoresDestination = false)
    {
        SetIdIgnoresDestination = setIdIgnoresDestination;
        _escs = escNodeIds.Select((id, i) => new SimEsc
        {
            NodeId = id,
            ThrottleChannel = (byte)(i + 1),
            CounterClockwise = i % 2 == 1,
        }).ToList();
        if (autoRun)
        {
            _ = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    public IReadOnlyList<SimEsc> Escs => _escs;
    public bool SetIdIgnoresDestination { get; }

    /// <summary>Service ids the simulated ESCs silently ignore (firmware that does not implement them).</summary>
    public HashSet<ushort> UnansweredServices { get; } = new();
    public List<ushort> Commands { get; } = new();
    public List<(ushort Type, byte[] Payload)> Requests { get; } = new();

    public void SendHeartbeat(bool armed)
    {
        var hb = MavlinkMessages.EncodeHeartbeat(2, 3); // quadrotor, ArduPilot
        hb[6] = armed ? MavlinkIds.MavModeFlagSafetyArmed : (byte)0;
        Emit(MavlinkIds.Heartbeat, hb);
    }

    public void ForwardBroadcast(byte sourceNode, DataTypeInfo type, byte[] payload, byte? transferId = null)
    {
        uint id = CanId.Message(16, type.Id, sourceNode);
        foreach (var f in TransferEncoder.Encode(id, transferId ?? _tid++, payload, type.Signature))
        {
            Forward(f);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        const int TickHz = 50;
        var rnd = new Random(1);
        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (tick % TickHz == 0)
                {
                    SendHeartbeat(armed: false);
                    if (_forwarding)
                    {
                        ForwardBroadcast(AutopilotNodeId, DataTypes.NodeStatus, NodeStatusPayload((uint)(tick / TickHz)));
                    }
                }
                if (tick % 10 == 0)
                {
                    SendEscTelemetry();
                }

                if (_forwarding)
                {
                    lock (_escs)
                    {
                        for (int i = 0; i < _escs.Count; i++)
                        {
                            var esc = _escs[i];
                            if (tick % TickHz == 0)
                            {
                                ForwardBroadcast(esc.NodeId, DataTypes.NodeStatus, NodeStatusPayload((uint)(tick / TickHz)));
                            }
                            if (Due(esc, DataTypes.HwStatusMsg1.Id, tick, TickHz))
                            {
                                var w = new BitWriter();
                                w.WriteU16((ushort)(1500 + 100 * i + rnd.Next(30)));
                                w.WriteU16(1200);
                                // real X8 G2 status does not follow the direction setting, so neither does this one
                                w.WriteU16(0);
                                ForwardBroadcast(esc.NodeId, DataTypes.HwStatusMsg1, w.ToArray());
                            }
                            if (Due(esc, DataTypes.HwStatusMsg2.Id, tick, TickHz))
                            {
                                var w = new BitWriter();
                                w.WriteSigned(503 - i, 16);
                                w.WriteSigned(42 + rnd.Next(5), 16);
                                w.WriteU8((byte)(30 + i));
                                ForwardBroadcast(esc.NodeId, DataTypes.HwStatusMsg2, w.ToArray());
                            }
                            if (Due(esc, DataTypes.HwStatusMsg3.Id, tick, TickHz))
                            {
                                ForwardBroadcast(esc.NodeId, DataTypes.HwStatusMsg3, [(byte)(35 + i), (byte)(31 + i), (byte)(40 + i)]);
                            }
                        }
                    }
                }

                tick++;
                await Task.Delay(1000 / TickHz, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Whether a message is due this tick at the ESC's configured rate (capped at the tick rate).</summary>
    private static bool Due(SimEsc esc, ushort messageId, int tick, int tickHz)
    {
        int hz = HwRate.ToHz(esc.RateCodes[messageId]) ?? 0;
        return hz > 0 && tick % Math.Max(1, tickHz / hz) == 0;
    }

    private static byte[] NodeStatusPayload(uint uptime)
    {
        var w = new BitWriter();
        w.WriteUnsigned(uptime, 32);
        w.WriteUnsigned(0, 2);
        w.WriteUnsigned(0, 3);
        w.WriteUnsigned(0, 3);
        w.WriteU16(0);
        return w.ToArray();
    }

    private void SendEscTelemetry()
    {
        var b = new byte[44];
        for (int i = 0; i < Math.Min(4, _escs.Count); i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 2), (ushort)(5030 - i * 10));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8 + i * 2), 420);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(24 + i * 2), (ushort)(1500 + 100 * i));
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32 + i * 2), 1);
            b[40 + i] = (byte)(30 + i);
        }
        Emit(MavlinkIds.EscTelemetry1To4, b);
    }

    private void Forward(CanFrame f) =>
        Emit(MavlinkIds.CanFrame, MavlinkMessages.EncodeCanFrame(255, 190, 0, f.Id | 0x80000000, f.Data));

    private void Emit(uint msgId, byte[] payload)
    {
        lock (_encoder)
        {
            _toApp.Writer.TryWrite(_encoder.Encode(msgId, payload));
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        foreach (var p in _parser.Push(buffer))
        {
            if (p.MessageId == MavlinkIds.CommandLong)
            {
                lock (Commands)
                {
                    Commands.Add(p.U16(28));
                }
                if (p.U16(28) == MavlinkIds.MavCmdCanForward)
                {
                    _forwarding = BitConverter.ToSingle(p.Bytes(0, 4)) >= 1;
                }
            }
            else if (p.MessageId == MavlinkIds.CanFrame)
            {
                var (rawId, _, data) = MavlinkMessages.DecodeCanFrame(p);
                if (_reassembler.Accept(new CanFrame(rawId & 0x1FFFFFFF, data), Environment.TickCount64) is { } t)
                {
                    Handle(t);
                }
            }
        }
    }

    private void Handle(Transfer t)
    {
        lock (_escs)
        {
            if (t.Kind == TransferKind.Broadcast && t.DataTypeId == DataTypes.HwGetEscId.Id)
            {
                foreach (var e in _escs)
                {
                    ForwardBroadcast(e.NodeId, DataTypes.HwGetEscId, [e.NodeId, e.ThrottleChannel], e.EscIdTransferId);
                    e.EscIdTransferId = (byte)((e.EscIdTransferId + 1) & 0x1F);
                }
                return;
            }
            if (t.Kind != TransferKind.Request)
            {
                return;
            }
            // every ESC with the addressed id answers (shared ids); with the X8 G2 quirk every ESC takes SetID
            if (UnansweredServices.Contains(t.DataTypeId))
            {
                return;
            }
            var targets = SetIdIgnoresDestination && t.DataTypeId == DataTypes.HwSetId.Id
                ? _escs.ToList()
                : _escs.Where(e => e.NodeId == t.DestinationNodeId).ToList();
            if (targets.Count == 0)
            {
                return;
            }

            lock (Requests)
            {
                Requests.Add((t.DataTypeId, t.Payload));
            }
            foreach (var esc in targets)
            {
                Respond(t, esc);
            }
        }
    }

    private void Respond(Transfer t, SimEsc esc)
    {
        byte replyFrom = esc.NodeId;
        byte[]? response = t.DataTypeId switch
        {
            1 => NodeInfoPayload(esc.NodeId),
            242 => MajorConfigPayload(esc),
            241 => Convert.FromHexString("40e20100f1fb09"),
            213 => SetDirection(esc, t.Payload[0]),
            214 => SetRate(esc, HwReportingFrequency.Decode(t.Payload)),
            215 => SetThrottleSource(esc, t.Payload[0]),
            212 => SetLed(esc, HwSetLed.Decode(t.Payload)),
            211 => SetBaud(esc, t.Payload[0]),
            210 => SetId(esc, HwSetId.Decode(t.Payload)),
            11 => t.Payload[0] switch
            {
                0 => Convert.FromHexString("01f2ffffffffffffff010e000000000000000164000000000000000102000000000000004d4f545f504f4c4553"),
                1 => Convert.FromHexString("020000c03f020000803e00004b"),
                _ => [0, 0, 0, 0],
            },
            _ => null,
        };
        if (response == null)
        {
            return;
        }
        var type = DataTypes.Find(true, t.DataTypeId)!;
        uint id = CanId.Service(16, (byte)type.Id, request: false, t.SourceNodeId, replyFrom);
        foreach (var f in TransferEncoder.Encode(id, t.TransferId, response, type.Signature))
        {
            Forward(f);
        }
    }

    private static byte[] MajorConfigPayload(SimEsc esc)
    {
        var w = new BitWriter();
        w.WriteUnsigned(esc.CounterClockwise ? 1u : 0u, 1);
        w.WriteUnsigned(esc.PwmThrottle ? 1u : 0u, 1);
        w.WriteUnsigned(esc.ThrottleChannel, 6);
        w.WriteUnsigned(esc.LedBlink, 5);
        w.WriteUnsigned(esc.LedColor, 3);
        w.WriteUnsigned(esc.RateCodes[DataTypes.HwStatusMsg2.Id], 4);
        w.WriteUnsigned(esc.RateCodes[DataTypes.HwStatusMsg1.Id], 4);
        w.WriteU16(0);
        w.WriteBytes([0, 0]);
        return w.ToArray();
    }

    private static byte[] SetDirection(SimEsc esc, byte direction)
    {
        if (direction != HwSetDirection.Query)
        {
            esc.CounterClockwise = direction == HwSetDirection.CounterClockwise;
        }
        return [esc.CounterClockwise ? HwSetDirection.CounterClockwise : HwSetDirection.Clockwise];
    }

    private static byte[] SetRate(SimEsc esc, HwReportingFrequency request)
    {
        if (request.Option == HwRate.Write && esc.RateCodes.ContainsKey(request.MessageId))
        {
            esc.RateCodes[request.MessageId] = request.RateCode;
        }
        byte code = esc.RateCodes.GetValueOrDefault(request.MessageId);
        var reply = HwReportingFrequency.EncodeWriteRequest(request.MessageId, code);
        reply[0] = request.Option;
        return reply;
    }

    private static byte[] SetThrottleSource(SimEsc esc, byte source)
    {
        esc.PwmThrottle = source == HwSetThrottleSource.Pwm;
        return [esc.PwmThrottle ? HwSetThrottleSource.Pwm : HwSetThrottleSource.Can];
    }

    private static byte[] SetBaud(SimEsc esc, byte code)
    {
        // the simulated bus has no bit rate, so the ESC stays reachable
        esc.BaudCode = code;
        return [];
    }

    private static byte[] SetLed(SimEsc esc, HwSetLed request)
    {
        esc.LedColor = request.Color;
        esc.LedBlink = request.Blink;
        return request.Encode();
    }

    private static byte[] SetId(SimEsc esc, HwSetId request)
    {
        // the reply still comes from the old node id; telemetry switches to the new one afterwards
        esc.ThrottleChannel = request.ThrottleChannel;
        esc.NodeId = request.NodeId;
        return request.Encode();
    }

    private static byte[] NodeInfoPayload(byte node)
    {
        var w = new BitWriter();
        w.WriteBytes(NodeStatusPayload(100));
        w.WriteBytes([1, 2, 0]);
        w.WriteBytes(new byte[12]);
        w.WriteBytes([3, 4]);
        w.WriteBytes(Enumerable.Range(node, 16).Select(i => (byte)i).ToArray());
        w.WriteU8(0);
        w.WriteBytes(Encoding.ASCII.GetBytes("com.hobbywing.esc.sim"));
        return w.ToArray();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pending.Length == 0)
        {
            _pending = await _toApp.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        int n = Math.Min(buffer.Length, _pending.Length);
        _pending.AsMemory(0, n).CopyTo(buffer);
        _pending = _pending[n..];
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        _cts.Cancel();
        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
