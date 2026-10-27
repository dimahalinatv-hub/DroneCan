using System.Collections.Concurrent;
using System.Diagnostics;

namespace HobbywingCan.Core.DroneCan;

/// <param name="IntendedNode">Node the request was addressed to.</param>
/// <param name="RespondingNode">Node id the extra response came from.</param>
/// <param name="DuplicateNodeId">True when the extra response came from the addressed node id itself (shared id).</param>
public sealed record UnexpectedResponse(ushort DataTypeId, byte IntendedNode, byte RespondingNode, bool DuplicateNodeId);

/// <summary>
/// What one node sent back for one service, addressed to us, counted per CAN frame so that "the node never
/// answered" can be told apart from "an answer arrived but was dropped".
/// </summary>
/// <param name="LastCrcMismatch">Payload of the newest answer that failed the CRC check, or null.</param>
public readonly record struct ResponseStats(long Frames, long Incomplete, long CrcErrors, byte[]? LastCrcMismatch)
{
    public ResponseStats Since(ResponseStats earlier) =>
        new(Frames - earlier.Frames, Incomplete - earlier.Incomplete, CrcErrors - earlier.CrcErrors,
            CrcErrors > earlier.CrcErrors ? LastCrcMismatch : null);
}

/// <summary>
/// Minimal DroneCAN node: reassembles incoming transfers, publishes broadcasts and
/// performs service calls (request → awaited response) on behalf of <see cref="LocalNodeId"/>.
/// </summary>
public sealed class DroneCanNode : IDisposable
{
    public const byte DefaultPriority = 24;

    private readonly ICanBus _bus;
    private readonly TransferReassembler _reassembler = new();
    private readonly object _rxLock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<(ushort Type, byte Node, byte Tid), TaskCompletionSource<Transfer>> _pending = new();
    private readonly ConcurrentDictionary<(TransferKind, ushort, byte), int> _transferIds = new();
    private readonly ConcurrentDictionary<(ushort Type, byte Node, byte Tid), long> _answered = new();
    private readonly List<RecentRequest> _recentRequests = new();
    private readonly ConcurrentDictionary<(ushort Type, byte Node), ResponseCounters> _responseCounters = new();

    private sealed class ResponseCounters
    {
        public long Frames;
        public long Incomplete;
        public long CrcErrors;
        public volatile byte[]? LastCrcMismatch;
    }

    /// <summary>How long after a request extra answers are still attributed to it.</summary>
    private const long UnexpectedWindowMs = 1500;

    private readonly record struct RecentRequest(ushort Type, byte Destination, byte Tid, long SentMs);

    public DroneCanNode(ICanBus bus, byte localNodeId)
    {
        if (localNodeId is < 1 or > 127)
        {
            throw new ArgumentOutOfRangeException(nameof(localNodeId));
        }
        _bus = bus;
        LocalNodeId = localNodeId;
        _reassembler.IncompleteTransfer += OnIncompleteTransfer;
        _reassembler.CrcMismatch += OnCrcMismatch;
        _bus.FrameReceived += OnFrame;
    }

    public byte LocalNodeId { get; }

    public long TransfersReceived { get; private set; }

    public long CrcErrors => _reassembler.CrcErrors;

    /// <summary>Multi-frame answers addressed to this node that arrived with frames missing.</summary>
    public long IncompleteResponses => Interlocked.Read(ref _incompleteResponses);

    private long _incompleteResponses;

    /// <summary>Raised on the transport's receive thread for every complete transfer seen on the bus.</summary>
    public event Action<Transfer>? TransferReceived;

    /// <summary>
    /// A response to one of our requests came from a node we did not address, or a second response came from
    /// the addressed node id. Either means a service call reached more than one device.
    /// </summary>
    public event Action<UnexpectedResponse>? UnexpectedResponse;

    public void Broadcast(DataTypeInfo type, byte[] payload, byte priority = DefaultPriority)
    {
        byte tid = NextTransferId(TransferKind.Broadcast, type.Id, 0);
        uint canId = CanId.Message(priority, type.Id, LocalNodeId);
        foreach (var frame in TransferEncoder.Encode(canId, tid, payload, type.Signature))
        {
            _bus.Send(frame);
        }
    }

    /// <summary>Sends a service request and waits for the matching response; returns null on timeout.</summary>
    /// <param name="alsoAcceptFrom">
    /// Another node id whose response to this request counts as the answer: SetID makes the ESC reply from its new id.
    /// </param>
    public async Task<Transfer?> RequestAsync(DataTypeInfo service, byte destination, byte[] payload, TimeSpan timeout,
        CancellationToken cancellationToken = default, byte priority = DefaultPriority, byte? alsoAcceptFrom = null)
    {
        if (!service.IsService)
        {
            throw new ArgumentException($"{service.Name} is not a service", nameof(service));
        }

        byte tid = NextTransferId(TransferKind.Request, service.Id, destination);
        var key = (service.Id, destination, tid);
        var tcs = new TaskCompletionSource<Transfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
        var altKey = (service.Id, alsoAcceptFrom ?? destination, tid);
        bool hasAlt = alsoAcceptFrom is { } alt && alt != destination && _pending.TryAdd(altKey, tcs);
        try
        {
            uint canId = CanId.Service(priority, (byte)service.Id, request: true, destination, LocalNodeId);
            lock (_recentRequests)
            {
                _recentRequests.Add(new RecentRequest(service.Id, destination, tid, _clock.ElapsedMilliseconds));
            }
            foreach (var frame in TransferEncoder.Encode(canId, tid, payload, service.Signature))
            {
                _bus.Send(frame);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
        finally
        {
            _pending.TryRemove(key, out _);
            if (hasAlt)
            {
                _pending.TryRemove(altKey, out _);
            }
        }
    }

    /// <summary>Like <see cref="RequestAsync"/> but retries on timeout.</summary>
    public async Task<Transfer?> RequestWithRetryAsync(DataTypeInfo service, byte destination, byte[] payload, TimeSpan timeout,
        int attempts, CancellationToken cancellationToken = default, byte? alsoAcceptFrom = null)
    {
        for (int i = 0; i < attempts; i++)
        {
            var response = await RequestAsync(service, destination, payload, timeout, cancellationToken,
                alsoAcceptFrom: alsoAcceptFrom).ConfigureAwait(false);
            if (response != null)
            {
                return response;
            }
        }
        return null;
    }

    private byte NextTransferId(TransferKind kind, ushort typeId, byte destination)
    {
        int value = _transferIds.AddOrUpdate((kind, typeId, destination), 0, (_, v) => (v + 1) & 0x1F);
        return (byte)value;
    }

    public ResponseStats GetResponseStats(ushort serviceId, byte sourceNodeId)
    {
        if (!_responseCounters.TryGetValue((serviceId, sourceNodeId), out var c))
        {
            return default;
        }
        return new ResponseStats(Interlocked.Read(ref c.Frames), Interlocked.Read(ref c.Incomplete),
            Interlocked.Read(ref c.CrcErrors), c.LastCrcMismatch);
    }

    /// <summary>Counters for a response addressed to this node, or null for any other frame.</summary>
    private ResponseCounters? CountersFor(uint canId) =>
        CanId.Kind(canId) == TransferKind.Response && CanId.DestinationNodeId(canId) == LocalNodeId
            ? _responseCounters.GetOrAdd((CanId.DataTypeId(canId), CanId.SourceNodeId(canId)), _ => new ResponseCounters())
            : null;

    private void OnIncompleteTransfer(uint canId)
    {
        if (CountersFor(canId) is { } c)
        {
            Interlocked.Increment(ref c.Incomplete);
            Interlocked.Increment(ref _incompleteResponses);
        }
    }

    private void OnCrcMismatch(uint canId, byte transferId, byte[] payload)
    {
        if (CountersFor(canId) is { } c)
        {
            c.LastCrcMismatch = payload;
            Interlocked.Increment(ref c.CrcErrors);
        }
    }

    private void OnFrame(CanFrame frame)
    {
        if (frame.Extended && CountersFor(frame.Id) is { } counters)
        {
            Interlocked.Increment(ref counters.Frames);
        }

        Transfer? transfer;
        lock (_rxLock)
        {
            transfer = _reassembler.Accept(frame, _clock.ElapsedMilliseconds);
        }
        if (transfer == null)
        {
            return;
        }

        TransfersReceived++;
        if (transfer.Kind == TransferKind.Response && transfer.DestinationNodeId == LocalNodeId)
        {
            MatchResponse(transfer);
        }
        TransferReceived?.Invoke(transfer);
    }

    private void MatchResponse(Transfer response)
    {
        long now = _clock.ElapsedMilliseconds;
        var key = (response.DataTypeId, response.SourceNodeId, response.TransferId);
        if (_pending.TryGetValue(key, out var tcs) && tcs.TrySetResult(response))
        {
            _answered[key] = now;
            if (_answered.Count > 256)
            {
                foreach (var old in _answered.Where(kv => now - kv.Value > UnexpectedWindowMs).Select(kv => kv.Key).ToList())
                {
                    _answered.TryRemove(old, out _);
                }
            }
            return;
        }

        // a second answer to the same request from the same node id: two devices share that id
        if (_answered.TryGetValue(key, out long answeredAt) && now - answeredAt < UnexpectedWindowMs)
        {
            UnexpectedResponse?.Invoke(new UnexpectedResponse(response.DataTypeId, response.SourceNodeId, response.SourceNodeId, DuplicateNodeId: true));
            return;
        }

        // an answer from a node we did not ask: that device ignores the destination node id
        RecentRequest? asked = null;
        lock (_recentRequests)
        {
            _recentRequests.RemoveAll(r => now - r.SentMs > UnexpectedWindowMs);
            if (!_recentRequests.Any(r => r.Type == response.DataTypeId && r.Tid == response.TransferId && r.Destination == response.SourceNodeId))
            {
                asked = _recentRequests.LastOrDefault(r => r.Type == response.DataTypeId && r.Tid == response.TransferId);
            }
        }
        if (asked is { } r)
        {
            UnexpectedResponse?.Invoke(new UnexpectedResponse(response.DataTypeId, r.Destination, response.SourceNodeId, DuplicateNodeId: false));
        }
    }

    public void Dispose()
    {
        _bus.FrameReceived -= OnFrame;
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetCanceled();
        }
    }
}
