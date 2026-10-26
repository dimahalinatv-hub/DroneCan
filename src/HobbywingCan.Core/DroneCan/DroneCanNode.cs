using System.Collections.Concurrent;
using System.Diagnostics;

namespace HobbywingCan.Core.DroneCan;

/// <param name="IntendedNode">Node the request was addressed to.</param>
/// <param name="RespondingNode">Node id the extra response came from.</param>
/// <param name="DuplicateNodeId">True when the extra response came from the addressed node id itself (shared id).</param>
public sealed record UnexpectedResponse(ushort DataTypeId, byte IntendedNode, byte RespondingNode, bool DuplicateNodeId);

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
        _bus.FrameReceived += OnFrame;
    }

    public byte LocalNodeId { get; }

    public long TransfersReceived { get; private set; }

    public long CrcErrors => _reassembler.CrcErrors;

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
    public async Task<Transfer?> RequestAsync(DataTypeInfo service, byte destination, byte[] payload, TimeSpan timeout,
        CancellationToken cancellationToken = default, byte priority = DefaultPriority)
    {
        if (!service.IsService)
        {
            throw new ArgumentException($"{service.Name} is not a service", nameof(service));
        }

        byte tid = NextTransferId(TransferKind.Request, service.Id, destination);
        var key = (service.Id, destination, tid);
        var tcs = new TaskCompletionSource<Transfer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;
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
        }
    }

    /// <summary>Like <see cref="RequestAsync"/> but retries on timeout.</summary>
    public async Task<Transfer?> RequestWithRetryAsync(DataTypeInfo service, byte destination, byte[] payload, TimeSpan timeout,
        int attempts, CancellationToken cancellationToken = default)
    {
        for (int i = 0; i < attempts; i++)
        {
            var response = await RequestAsync(service, destination, payload, timeout, cancellationToken).ConfigureAwait(false);
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

    private void OnFrame(CanFrame frame)
    {
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
