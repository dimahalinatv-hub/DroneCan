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

    internal uint? LastUptime;
    internal readonly Queue<DateTime> UptimeRegressions = new();
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
                    _node.Broadcast(DataTypes.HwGetEscId, HwGetEscId.EncodeQuery());
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
