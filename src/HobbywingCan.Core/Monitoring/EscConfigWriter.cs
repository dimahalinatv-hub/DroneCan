using HobbywingCan.Core.DroneCan;
using HobbywingCan.Core.Messages;

namespace HobbywingCan.Core.Monitoring;

/// <param name="Success">The ESC answered and echoed the requested value.</param>
/// <param name="Message">Human-readable outcome for the log.</param>
/// <param name="AlsoAnsweredBy">Other node ids that answered (and so probably applied) the same request.</param>
/// <param name="SameIdAnswers">How many devices answered from the addressed node id (0 when none did).</param>
public sealed record WriteResult(bool Success, string Message, IReadOnlyList<byte> AlsoAnsweredBy, int SameIdAnswers)
{
    /// <summary>More than one device answered from the addressed node id.</summary>
    public bool DuplicateNodeId => SameIdAnswers > 1;

    public bool AffectedOthers => AlsoAnsweredBy.Count > 0 || DuplicateNodeId;
}

/// <summary>Who answered a harmless request addressed to one ESC.</summary>
/// <param name="SameIdAnswers">
/// How many devices answered from the probed node id. A lower bound: identical answers sent at the same instant
/// merge into one CAN frame.
/// </param>
/// <param name="ProbedWith">Service that got an answer; null when no read request was answered at all.</param>
public sealed record AddressingProbe(bool TargetAnswered, IReadOnlyList<byte> OtherResponders, int SameIdAnswers, string? ProbedWith)
{
    /// <summary>More than one device answered from the probed node id (shared id).</summary>
    public bool DuplicateNodeId => SameIdAnswers > 1;

    /// <summary>Nobody answered any read request, so it is unknown which ESCs a write reaches.</summary>
    public bool Inconclusive => !TargetAnswered && OtherResponders.Count == 0 && !DuplicateNodeId;

    /// <summary>Only the addressed ESC answered, so a write would reach only that ESC.</summary>
    public bool IsolatedTarget => TargetAnswered && OtherResponders.Count == 0 && !DuplicateNodeId;
}

/// <summary>
/// Changes Hobbywing ESC settings via the com.hobbywing.esc.Set* services. Each call checks that the
/// response echoes the requested value and reports any other ESC that answered too: Hobbywing X8 G2
/// firmware has been seen applying SetID to every ESC on the bus regardless of the destination node.
/// Callers are responsible for only doing this while disarmed.
/// </summary>
public sealed class EscConfigWriter(DroneCanNode node)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ExtraAnswerWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(400);
    private const int Attempts = 3;

    /// <param name="Responder">Node the answer came from: the addressed one, or the new id after SetID.</param>
    private sealed record CallResult(byte[]? Payload, byte Responder, IReadOnlyList<byte> Others, int SameIdAnswers)
    {
        public bool Duplicate => SameIdAnswers > 1;
    }

    /// <summary>
    /// Sends read-only requests to <paramref name="nodeId"/> and reports who answered. Several services are
    /// tried because not every ESC firmware implements each of them; the first one that gets any answer wins.
    /// </summary>
    public async Task<AddressingProbe> ProbeAddressingAsync(byte nodeId, CancellationToken ct = default)
    {
        (DataTypeInfo Service, byte[] Request)[] probes =
        [
            (DataTypes.HwSetReportingFrequency, HwReportingFrequency.EncodeReadRequest(DataTypes.HwStatusMsg1.Id)),
            (DataTypes.HwGetMajorConfig, HwMajorConfig.EncodeRequest()),
            (DataTypes.GetNodeInfo, []),
        ];
        foreach (var (service, request) in probes)
        {
            var call = await Call(service, nodeId, request, ct, ProbeTimeout, attempts: 1);
            if (call.Payload != null || call.Others.Count > 0 || call.Duplicate)
            {
                return new AddressingProbe(call.Payload != null, call.Others, call.SameIdAnswers, service.Name);
            }
        }
        return new AddressingProbe(false, [], 0, null);
    }

    public async Task<WriteResult> SetReportingRateAsync(byte nodeId, ushort messageId, byte rateCode, CancellationToken ct = default)
    {
        string what = $"StatusMsg{messageId - 20049} rate → {HwRate.ToText(rateCode)}";
        var call = await Call(DataTypes.HwSetReportingFrequency, nodeId,
            HwReportingFrequency.EncodeWriteRequest(messageId, rateCode), ct);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        var r = HwReportingFrequency.Decode(call.Payload);
        return Check(nodeId, what, r.MessageId == messageId && r.RateCode == rateCode,
            $"msg {r.MessageId}, {HwRate.ToText(r.RateCode)}", call);
    }

    public async Task<WriteResult> SetThrottleSourceAsync(byte nodeId, byte source, CancellationToken ct = default)
    {
        string what = $"throttle source → {HwSetThrottleSource.ToText(source)}";
        var call = await Call(DataTypes.HwSetThrottleSource, nodeId, HwSetThrottleSource.Encode(source), ct);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        var actual = HwSetThrottleSource.DecodeResponse(call.Payload);
        return Check(nodeId, what, actual == source, HwSetThrottleSource.ToText(actual), call);
    }

    public async Task<WriteResult> SetDirectionAsync(byte nodeId, byte direction, CancellationToken ct = default)
    {
        string what = $"direction → {HwSetDirection.ToText(direction)}";
        var call = await Call(DataTypes.HwSetDirection, nodeId, HwSetDirection.Encode(direction), ct);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        var actual = HwSetDirection.DecodeResponse(call.Payload);
        return Check(nodeId, what, actual == direction, HwSetDirection.ToText(actual), call);
    }

    /// <summary>SetID always carries both values; pass the current ones for whatever should not change.</summary>
    public async Task<WriteResult> SetIdAsync(byte nodeId, byte newNodeId, byte throttleChannel, CancellationToken ct = default)
    {
        string what = $"node ID {newNodeId}, throttle channel {throttleChannel}";
        var request = new HwSetId(newNodeId, throttleChannel);
        // the ESC takes the new id before it answers, so the reply comes from the new node
        var call = await Call(DataTypes.HwSetId, nodeId, request.Encode(), ct,
            alsoAcceptFrom: newNodeId != nodeId ? newNodeId : null);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        var actual = HwSetId.Decode(call.Payload);
        if (call.Responder != nodeId)
        {
            what += $" (now answers as node {call.Responder})";
        }
        return Check(nodeId, what, actual == request, $"node ID {actual.NodeId}, throttle channel {actual.ThrottleChannel}", call);
    }

    public async Task<WriteResult> SetLedAsync(byte nodeId, byte color, byte blink, bool save, CancellationToken ct = default)
    {
        var request = new HwSetLed(save ? HwSetLed.OptionSave : (byte)0, color, blink);
        string what = $"LED color {color}, blink {blink}{(save ? ", saved" : "")}";
        var call = await Call(DataTypes.HwSetLed, nodeId, request.Encode(), ct);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        var actual = HwSetLed.Decode(call.Payload);
        return Check(nodeId, what, actual.Color == color && actual.Blink == blink, $"color {actual.Color}, blink {actual.Blink}", call);
    }

    /// <summary>The response is empty, so an answer is all there is to check.</summary>
    public async Task<WriteResult> SetBaudAsync(byte nodeId, byte code, CancellationToken ct = default)
    {
        string what = $"CAN speed → {HwSetBaud.ToText(code)}";
        var call = await Call(DataTypes.HwSetBaud, nodeId, HwSetBaud.Encode(code), ct);
        if (call.Payload == null)
        {
            return NoResponse(nodeId, what, call);
        }
        return Check(nodeId, what, true, "", call);
    }

    /// <summary>Performs the request and collects extra answers from other nodes (or duplicate ids) meanwhile.</summary>
    /// <param name="alsoAcceptFrom">node id whose answer counts as the addressed ESC's (the new id after SetID)</param>
    private async Task<CallResult> Call(DataTypeInfo service, byte nodeId, byte[] request, CancellationToken ct,
        TimeSpan? timeout = null, int attempts = Attempts, byte? alsoAcceptFrom = null)
    {
        var others = new HashSet<byte>();
        int duplicates = 0;
        void OnUnexpected(UnexpectedResponse u)
        {
            if (u.DataTypeId != service.Id || u.IntendedNode != nodeId && u.IntendedNode != alsoAcceptFrom)
            {
                return;
            }
            lock (others)
            {
                if (u.DuplicateNodeId)
                {
                    // every extra answer from the addressed id is one more device sharing it
                    duplicates++;
                }
                else if (u.RespondingNode == alsoAcceptFrom)
                {
                    // a late answer from the new id to an attempt that had already timed out: the same ESC, not another one
                }
                else
                {
                    others.Add(u.RespondingNode);
                }
            }
        }

        node.UnexpectedResponse += OnUnexpected;
        try
        {
            var response = await node.RequestWithRetryAsync(service, nodeId, request, timeout ?? Timeout, attempts, ct, alsoAcceptFrom);
            // other ESCs may answer a little later than the addressed one
            await Task.Delay(ExtraAnswerWindow, ct);
            lock (others)
            {
                return new CallResult(response?.Payload, response?.SourceNodeId ?? nodeId, others.OrderBy(n => n).ToList(),
                    response != null ? 1 + duplicates : duplicates);
            }
        }
        finally
        {
            node.UnexpectedResponse -= OnUnexpected;
        }
    }

    private static WriteResult NoResponse(byte nodeId, string what, CallResult call) =>
        Result(false, $"Node {nodeId}: {what} — no response", call);

    private static WriteResult Check(byte nodeId, string what, bool ok, string actual, CallResult call) => ok
        ? Result(true, $"Node {nodeId}: {what} — OK", call)
        : Result(false, $"Node {nodeId}: {what} — ESC answered {actual}", call);

    private static WriteResult Result(bool success, string message, CallResult call)
    {
        if (call.Others.Count > 0)
        {
            message += $". WARNING: node(s) {string.Join(", ", call.Others)} answered too and probably applied it as well";
        }
        if (call.Duplicate)
        {
            message += $". WARNING: {call.SameIdAnswers} ESCs answered from this node id";
        }
        return new WriteResult(success, message, call.Others, call.SameIdAnswers);
    }
}
