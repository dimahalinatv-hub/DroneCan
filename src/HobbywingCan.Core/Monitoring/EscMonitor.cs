using System.Collections.Concurrent;
using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Link;
using HobbywingCan.Core.Mavlink;
using HobbywingCan.Core.Messages;

namespace HobbywingCan.Core.Monitoring;

/// <summary>Latest known state of one DroneCAN node (typically one Hobbywing ESC).</summary>
public sealed class NodeState(byte nodeId)
{
    public byte NodeId { get; } = nodeId;
    public DateTime LastSeenUtc { get; internal set; }

    public NodeStatus? Status { get; internal set; }
    public NodeInfo? Info { get; internal set; }
    public byte? ThrottleChannel { get; internal set; }

    /// <summary>Last GetMajorConfig response seen for this node (from any requester).</summary>
    public HwMajorConfig? MajorConfig { get; internal set; }

    /// <summary>Last reporting-rate code per Hobbywing status message id, from SetReportingFrequency responses.</summary>
    public ConcurrentDictionary<ushort, byte> ReportingRateCodes { get; } = new();

    /// <summary>
    /// Direction setting as answered to com.hobbywing.esc.SetDirection (0 = CW, 1 = CCW). This is the ESC's
    /// configured direction; the physical rotation also depends on how the motor phases are wired.
    /// </summary>
    public byte? DirectionSetting { get; internal set; }

    /// <summary>
    /// Best known direction setting: SetDirection answer, else GetMajorConfig; null when neither was read.
    /// StatusMsg1 bit 14 is deliberately not used — it does not reflect the setting on X8 G2.
    /// </summary>
    public bool? CounterClockwise =>
        DirectionSetting is { } d ? d == HwSetDirection.CounterClockwise : MajorConfig?.CounterClockwise;

    /// <summary>
    /// Throttle source as answered to com.hobbywing.esc.SetThrottleSource. There is no read-only query, so this
    /// is only known after a write; cleared by a newer GetMajorConfig response.
    /// </summary>
    public byte? ThrottleSourceSetting { get; internal set; }

    /// <summary>LED setting as answered to com.hobbywing.esc.SetLED; cleared by a newer GetMajorConfig response.</summary>
    public HwSetLed? LedSetting { get; internal set; }

    /// <summary>Best known throttle source: newest of SetThrottleSource answer and GetMajorConfig; null when neither was seen.</summary>
    public bool? ThrottleSourcePwm =>
        ThrottleSourceSetting is { } s ? s == HwSetThrottleSource.Pwm : MajorConfig?.ThrottleSourcePwm;

    /// <summary>Best known LED color (SetLED answer, else GetMajorConfig).</summary>
    public byte? LedColor => LedSetting?.Color ?? MajorConfig?.LedColor;

    /// <summary>Best known LED blink mode (SetLED answer, else GetMajorConfig led_status).</summary>
    public byte? LedBlink => LedSetting?.Blink ?? MajorConfig?.LedStatus;

    public HwStatusMsg1? Msg1 { get; internal set; }
    public HwStatusMsg2? Msg2 { get; internal set; }
    public HwStatusMsg3? Msg3 { get; internal set; }
    public EscStatus? EscStatus { get; internal set; }
    public EscStatusExtended? EscStatusExtended { get; internal set; }

    public long Msg1Count { get; internal set; }
    public long Msg2Count { get; internal set; }
    public long Msg3Count { get; internal set; }
    public long EscStatusCount { get; internal set; }

    public bool IsHobbywing => Msg1 != null || Msg2 != null || Msg3 != null || ThrottleChannel != null;

    /// <summary>More than one device seems to use this node id (double answers, uptime jumping back and forth).</summary>
    public bool DuplicateSuspected { get; internal set; }

    /// <summary>This node answered a request addressed to another node, i.e. it ignores the destination.</summary>
    public bool IgnoresDestination { get; internal set; }

    /// <summary>
    /// How many devices answer GetEscID as this node id: the most copies of one reply transfer id (see
    /// <see cref="EscIdTrack"/>). Counting replies per query is not enough: ArduPilot polls GetEscID too, and its
    /// queries may never reach this app, so one ESC can answer twice between two of our queries. Sequences are not
    /// added up either: on the real X8 G2 one ESC gives one sequence per poller. An ESC that is out of step with the
    /// others (plugged in later) has the same transfer ids only by chance, so interleaved NodeStatus uptimes count
    /// as at least two devices. Null until a reply was seen (GetEscID is only polled while disarmed).
    /// </summary>
    public int? DeviceCount
    {
        get
        {
            int? copies;
            lock (EscIdTracks)
            {
                copies = EscIdTracks.Count == 0 ? null : EscIdTracks.Max(t => t.Devices);
            }
            return UptimeInterleaved ? Math.Max(copies ?? 0, 2) : copies;
        }
    }

    /// <summary>What <see cref="DeviceCount"/> is based on, for the log: copies per reply sequence and the uptime.</summary>
    public string DeviceCountEvidence
    {
        get
        {
            string tracks;
            lock (EscIdTracks)
            {
                tracks = string.Join(", ", EscIdTracks.Select(t => $"id {t.LastTransferId} ×{t.Devices}"));
            }
            return $"GetEscID reply sequences: {(tracks.Length > 0 ? tracks : "none")}" +
                (UptimeInterleaved ? "; NodeStatus uptime jumps back and forth" : "");
        }
    }

    /// <summary>NodeStatus uptimes of two devices interleaved recently.</summary>
    private bool UptimeInterleaved => UptimeInterleavedUtc is { } t && DateTime.UtcNow - t < TimeSpan.FromSeconds(15);

    internal uint? LastUptime;
    internal readonly Queue<DateTime> UptimeRegressions = new();
    internal DateTime? UptimeInterleavedUtc;

    internal readonly List<EscIdTrack> EscIdTracks = new();
}

/// <summary>
/// One sequence of GetEscID reply transfer ids, one higher per reply. Firmware either numbers its replies itself or,
/// like the X8 G2 seems to, copies the id of the query, which gives one sequence per poller (this app, ArduPilot).
/// Either way every ESC on the node id answers each query with the same id at the same moment, so the devices are
/// the copies of each id. Identical frames sent at the same instant merge on CAN, so the most copies over the last
/// few ids is taken.
/// </summary>
internal sealed class EscIdTrack(byte transferId, DateTime nowUtc)
{
    /// <summary>Ids the copy count is taken over: an unplugged lockstep ESC drops out after about this many replies.</summary>
    private const int History = 10;

    private readonly Queue<int> _recentCopies = new();

    public byte LastTransferId { get; private set; } = transferId;
    public DateTime LastTransferIdUtc { get; private set; } = nowUtc;
    public DateTime LastSeenUtc { get; private set; } = nowUtc;
    public int Copies { get; private set; } = 1;

    public int Devices => _recentCopies.Count == 0 ? Copies : Math.Max(Copies, _recentCopies.Max());

    /// <summary>Distance from the last id to <paramref name="transferId"/> (5-bit, wraps).</summary>
    public int GapTo(byte transferId) => (transferId - LastTransferId) & 0x1F;

    public void AddCopy(DateTime nowUtc)
    {
        Copies++;
        LastSeenUtc = nowUtc;
    }

    public void Advance(byte transferId, DateTime nowUtc)
    {
        _recentCopies.Enqueue(Copies);
        while (_recentCopies.Count > History)
        {
            _recentCopies.Dequeue();
        }
        LastTransferId = transferId;
        LastTransferIdUtc = nowUtc;
        LastSeenUtc = nowUtc;
        Copies = 1;
    }
}

/// <summary>How many transfers of one type one node has sent (bus diagnostics).</summary>
public sealed record TrafficEntry(byte SourceNodeId, TransferKind Kind, ushort DataTypeId, string TypeName, long Count, DateTime LastUtc);

/// <summary>
/// Collects live telemetry from DroneCAN transfers (Hobbywing StatusMsg1/2/3, standard esc.Status,
/// NodeStatus, GetEscID) and from ArduPilot's own ESC_TELEMETRY messages.
/// </summary>
public sealed class EscMonitor : IDisposable
{
    private readonly DroneCanNode _node;
    private readonly MavlinkCanLink _link;
    private readonly ConcurrentDictionary<byte, NodeState> _nodes = new();
    private readonly AutopilotEscTelemetry?[] _autopilotEsc = new AutopilotEscTelemetry?[12];
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<(byte Node, TransferKind Kind, ushort Type), TrafficCounter> _traffic = new();

    /// <summary>Copies of one reply from lockstep ESCs arrive together; later ones are a new reply.</summary>
    private static readonly TimeSpan EscIdCopyWindow = TimeSpan.FromMilliseconds(50);

    /// <summary>A track with no reply for this long while the node still answers is an unplugged ESC.</summary>
    private static readonly TimeSpan EscIdTrackTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Lost replies skip ids; a jump of up to this many still continues the same track.</summary>
    private const int EscIdMaxGap = 3;

    private sealed class TrafficCounter
    {
        public long Count;
        public DateTime LastUtc;
    }

    public EscMonitor(DroneCanNode node, MavlinkCanLink link)
    {
        _node = node;
        _link = link;
        _node.TransferReceived += OnTransfer;
        _node.UnexpectedResponse += OnUnexpectedResponse;
        _link.EscTelemetryReceived += OnAutopilotEsc;
        _ = Task.Run(() => PollEscIdsAsync(_cts.Token));
    }

    /// <summary>
    /// Periodically broadcast GetEscID (like ArduPilot and the DroneCAN GUI do) to learn each ESC's throttle channel.
    /// Never done while armed: ArduPilot notes this can make some Hobbywing ESCs stutter.
    /// </summary>
    public bool PollEscIds { get; set; } = true;

    public IReadOnlyList<NodeState> Nodes => _nodes.Values.OrderBy(n => n.NodeId).ToList();

    public IReadOnlyList<AutopilotEscTelemetry> AutopilotEsc
    {
        get
        {
            lock (_autopilotEsc)
            {
                return _autopilotEsc.Where(e => e != null).Select(e => e!).ToList();
            }
        }
    }

    public IReadOnlyList<TrafficEntry> Traffic => _traffic
        .Select(kv => new TrafficEntry(kv.Key.Node, kv.Key.Kind, kv.Key.Type,
            DataTypeNames.Get(kv.Key.Kind != TransferKind.Broadcast, kv.Key.Type), Interlocked.Read(ref kv.Value.Count), kv.Value.LastUtc))
        .OrderBy(t => t.SourceNodeId).ThenBy(t => t.Kind).ThenBy(t => t.DataTypeId)
        .ToList();

    public NodeState? Get(byte nodeId) => _nodes.GetValueOrDefault(nodeId);

    internal NodeState GetOrAdd(byte nodeId) => _nodes.GetOrAdd(nodeId, id => new NodeState(id));

    private void OnTransfer(Transfer t)
    {
        if (t.SourceNodeId == _node.LocalNodeId)
        {
            return;
        }

        var counter = _traffic.GetOrAdd((t.SourceNodeId, t.Kind, t.DataTypeId), _ => new TrafficCounter());
        Interlocked.Increment(ref counter.Count);
        counter.LastUtc = DateTime.UtcNow;

        if (t.SourceNodeId == 0)
        {
            return;
        }

        try
        {
            if (t.Kind == TransferKind.Broadcast)
            {
                HandleBroadcast(t);
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.GetNodeInfo.Id)
            {
                // also pick up GetNodeInfo responses addressed to others (e.g. ArduPilot's DNA server)
                var state = GetOrAdd(t.SourceNodeId);
                state.Info = NodeInfo.Decode(t.Payload);
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.HwGetMajorConfig.Id)
            {
                var state = GetOrAdd(t.SourceNodeId);
                var config = HwMajorConfig.Decode(t.Payload);
                state.MajorConfig = config;
                // the config is newer than any earlier write answer
                state.ThrottleSourceSetting = null;
                state.LedSetting = null;
                state.ThrottleChannel = config.ThrottleChannel;
                state.ReportingRateCodes[DataTypes.HwStatusMsg1.Id] = config.Msg1RateCode;
                state.ReportingRateCodes[DataTypes.HwStatusMsg2.Id] = config.Msg2RateCode;
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.HwSetReportingFrequency.Id)
            {
                var rate = HwReportingFrequency.Decode(t.Payload);
                GetOrAdd(t.SourceNodeId).ReportingRateCodes[rate.MessageId] = rate.RateCode;
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.HwSetDirection.Id)
            {
                // answers both the 255 query and actual writes with the ESC's current setting
                GetOrAdd(t.SourceNodeId).DirectionSetting = HwSetDirection.DecodeResponse(t.Payload);
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.HwSetThrottleSource.Id)
            {
                // the ESC answers with the source it now uses; the only way to learn it without GetMajorConfig
                byte source = HwSetThrottleSource.DecodeResponse(t.Payload);
                if (source is HwSetThrottleSource.Can or HwSetThrottleSource.Pwm)
                {
                    GetOrAdd(t.SourceNodeId).ThrottleSourceSetting = source;
                }
            }
            else if (t.Kind == TransferKind.Response && t.DataTypeId == DataTypes.HwSetLed.Id && t.Payload.Length >= 3)
            {
                GetOrAdd(t.SourceNodeId).LedSetting = HwSetLed.Decode(t.Payload);
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            // malformed payload from a foreign node, ignore
        }
    }

    private void HandleBroadcast(Transfer t)
    {
        ushort type = t.DataTypeId;
        if (type != DataTypes.NodeStatus.Id && type != DataTypes.EscStatus.Id && type != DataTypes.EscStatusExtended.Id &&
            type != DataTypes.HwGetEscId.Id &&
            type != DataTypes.HwStatusMsg1.Id && type != DataTypes.HwStatusMsg2.Id && type != DataTypes.HwStatusMsg3.Id)
        {
            return;
        }

        var state = GetOrAdd(t.SourceNodeId);
        state.LastSeenUtc = DateTime.UtcNow;

        if (type == DataTypes.NodeStatus.Id)
        {
            state.Status = NodeStatus.Decode(t.Payload);
            TrackUptime(state, state.Status.UptimeSec);
        }
        else if (type == DataTypes.EscStatus.Id)
        {
            state.EscStatus = EscStatus.Decode(t.Payload);
            state.EscStatusCount++;
        }
        else if (type == DataTypes.EscStatusExtended.Id)
        {
            state.EscStatusExtended = EscStatusExtended.Decode(t.Payload);
        }
        else if (type == DataTypes.HwGetEscId.Id)
        {
            if (HwGetEscId.TryDecodeReply(t.SourceNodeId, t.Payload, out var channel))
            {
                state.ThrottleChannel = channel;
                CountEscIdReply(state, t.TransferId);
            }
        }
        else if (type == DataTypes.HwStatusMsg1.Id)
        {
            state.Msg1 = HwStatusMsg1.Decode(t.Payload);
            state.Msg1Count++;
        }
        else if (type == DataTypes.HwStatusMsg2.Id)
        {
            state.Msg2 = HwStatusMsg2.Decode(t.Payload);
            state.Msg2Count++;
        }
        else
        {
            state.Msg3 = HwStatusMsg3.Decode(t.Payload);
            state.Msg3Count++;
        }
    }

    private void OnUnexpectedResponse(UnexpectedResponse u)
    {
        var state = GetOrAdd(u.RespondingNode);
        if (u.DuplicateNodeId)
        {
            state.DuplicateSuspected = true;
        }
        else
        {
            state.IgnoresDestination = true;
        }
    }

    /// <summary>
    /// Two devices sharing a node id interleave their NodeStatus, so the uptime keeps going backwards.
    /// A single regression is just a reboot; several within 10 s mean a shared id.
    /// </summary>
    private static void TrackUptime(NodeState state, uint uptime)
    {
        var now = DateTime.UtcNow;
        if (state.LastUptime is { } last && uptime + 1 < last)
        {
            state.UptimeRegressions.Enqueue(now);
        }
        state.LastUptime = uptime;
        while (state.UptimeRegressions.Count > 0 && now - state.UptimeRegressions.Peek() > TimeSpan.FromSeconds(10))
        {
            state.UptimeRegressions.Dequeue();
        }
        if (state.UptimeRegressions.Count >= 2)
        {
            state.DuplicateSuspected = true;
            state.UptimeInterleavedUtc = now;
        }
    }

    /// <summary>Broadcasts one GetEscID query; every ESC answers with its node id and throttle channel.</summary>
    public void QueryEscIds() => _node.Broadcast(DataTypes.HwGetEscId, HwGetEscId.EncodeQuery());

    /// <summary>Assigns one GetEscID reply to the track of the ESC(s) that sent it.</summary>
    private static void CountEscIdReply(NodeState state, byte transferId)
    {
        var now = DateTime.UtcNow;
        lock (state.EscIdTracks)
        {
            var tracks = state.EscIdTracks;
            tracks.RemoveAll(t => now - t.LastSeenUtc > EscIdTrackTimeout);

            var copy = tracks.FirstOrDefault(t => t.LastTransferId == transferId && now - t.LastTransferIdUtc <= EscIdCopyWindow);
            if (copy != null)
            {
                copy.AddCopy(now);
            }
            else if (tracks
                         // the nearest next id; of two equally near, the track waiting longest for its reply
                         .Where(t => t.GapTo(transferId) is >= 1 and <= EscIdMaxGap)
                         .OrderBy(t => t.GapTo(transferId)).ThenBy(t => t.LastSeenUtc)
                         .FirstOrDefault() is { } next)
            {
                next.Advance(transferId, now);
            }
            else if (tracks.FirstOrDefault(t => t.LastTransferId == transferId) is { } same)
            {
                // firmware that does not count its transfer ids
                same.Advance(transferId, now);
            }
            else
            {
                tracks.Add(new EscIdTrack(transferId, now));
            }
        }
        if (state.DeviceCount >= 2)
        {
            state.DuplicateSuspected = true;
        }
    }

    private void OnAutopilotEsc(AutopilotEscTelemetry[] block)
    {
        lock (_autopilotEsc)
        {
            foreach (var e in block)
            {
                // ArduPilot fills unused slots with zeros
                if (e.Count != 0 || e.Voltage != 0 || e.Rpm != 0 || e.TemperatureC != 0)
                {
                    _autopilotEsc[e.Index] = e;
                }
            }
        }
    }

    private async Task PollEscIdsAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (PollEscIds && _link.Vehicle != null && !_link.IsArmed)
                {
                    QueryEscIds();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _node.TransferReceived -= OnTransfer;
        _node.UnexpectedResponse -= OnUnexpectedResponse;
        _link.EscTelemetryReceived -= OnAutopilotEsc;
    }
}
